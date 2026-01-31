from __future__ import annotations

import asyncio
import logging
from typing import Any, Dict, Optional

from fastapi import BackgroundTasks, FastAPI, File, Header, HTTPException, Query, UploadFile
from fastapi.responses import JSONResponse

from config import Settings
from db import Db, Tenant
from discord_webhook import DiscordWebhookClient
from ocr.router import run_ocr
from tribelog.classify import classify_event
from tribelog.parse import parse_lines

logger = logging.getLogger("screenshots_api")
logging.basicConfig(level=logging.INFO)


def _get_key(x_gl_key: Optional[str], x_api_key: Optional[str]) -> str:
    return ((x_gl_key or x_api_key or "")).strip()


async def _resolve_tenant_or_401(app: FastAPI, settings: Settings, key: str) -> Tenant:
    if settings.tenants_enabled:
        if not key:
            raise HTTPException(status_code=401, detail="Missing API key")
        if app.state.db.pool is None:
            # In multi-tenant mode, DB is required.
            raise HTTPException(status_code=503, detail="Database not configured")
        tenant = await app.state.db.resolve_tenant_by_key(key)
        if tenant is None and settings.tenants_bootstrap_legacy and settings.gl_shared_secret and key == settings.gl_shared_secret:
            await app.state.db.ensure_legacy_tenant(
                legacy_secret=settings.gl_shared_secret,
                legacy_tenant_name=settings.legacy_tenant_name,
                legacy_webhook_url=settings.alert_discord_webhook_url,
                legacy_log_posting_enabled=settings.log_posting_enabled,
                legacy_post_delay_seconds=settings.post_delay_seconds,
                legacy_critical_ping_enabled=settings.critical_ping_enabled,
                legacy_critical_ping_role_id=settings.critical_ping_role_id,
                legacy_ping_all_critical=settings.ping_all_critical,
                legacy_ping_categories=sorted(settings.ping_categories),
            )
            tenant = await app.state.db.resolve_tenant_by_key(key)
        if tenant is None or not tenant.is_enabled:
            raise HTTPException(status_code=401, detail="Invalid API key")
        return tenant

    # Legacy mode: optional shared secret
    if settings.gl_shared_secret and key != settings.gl_shared_secret:
        raise HTTPException(status_code=401, detail="Invalid API key")

    # Prefer DB-backed default tenant if available
    if app.state.db.pool is not None:
        t = await app.state.db.get_default_tenant()
        if t is not None:
            return t

    # Fallback: no DB configured (dev only) — a synthetic tenant that will not dedupe/insert.
    return Tenant(
        id=0,
        name="default",
        api_key_hash=None,
        webhook_url=settings.alert_discord_webhook_url,
        is_enabled=True,
        log_posting_enabled=settings.log_posting_enabled,
        post_delay_seconds=settings.post_delay_seconds,
        critical_ping_enabled=settings.critical_ping_enabled,
        critical_ping_role_id=settings.critical_ping_role_id,
        ping_all_critical=settings.ping_all_critical,
        ping_categories=set(settings.ping_categories),
    )


def _get_webhook_client(app: FastAPI, webhook_url: str) -> Optional[DiscordWebhookClient]:
    url = (webhook_url or "").strip()
    if not url:
        return None
    cache: Dict[str, DiscordWebhookClient] = app.state.webhook_clients
    client = cache.get(url)
    if client is None:
        client = DiscordWebhookClient(webhook_url=url, post_delay_seconds=0.0)
        cache[url] = client
    return client


def _should_ping(tenant: Tenant, ev_category: str, ev_severity: str, critical_ping: bool) -> bool:
    if not critical_ping:
        return False
    if not tenant.critical_ping_enabled:
        return False
    if (ev_severity or "").upper() != "CRITICAL":
        return False
    if tenant.ping_all_critical:
        return True
    return (ev_category or "") in tenant.ping_categories


async def _post_events(
    *,
    app: FastAPI,
    tenant: Tenant,
    events,
    critical_ping: bool,
    env: str,
) -> int:
    webhook = _get_webhook_client(app, tenant.webhook_url)
    if webhook is None:
        return 0

    posted = 0
    for ev in events:
        mention = _should_ping(tenant, ev.category, ev.severity, critical_ping=critical_ping)
        role_id = tenant.critical_ping_role_id.strip() if tenant.critical_ping_role_id else ""
        await webhook.post_event_from_parsed(
            ev,
            mention=mention and bool(role_id),
            mention_role_id=role_id,
            env=env,
        )
        posted += 1
        if tenant.post_delay_seconds and tenant.post_delay_seconds > 0:
            await asyncio.sleep(float(tenant.post_delay_seconds))
    return posted


def create_app() -> FastAPI:
    settings = Settings.from_env()

    app = FastAPI(title="screenshots-api", version="0.1.0")

    app.state.settings = settings
    app.state.db = Db(settings.database_url)
    app.state.webhook_clients = {}  # webhook_url -> DiscordWebhookClient

    @app.on_event("startup")
    async def _startup() -> None:
        try:
            await app.state.db.start(
                legacy_secret=settings.gl_shared_secret,
                legacy_tenant_name=settings.legacy_tenant_name,
                legacy_webhook_url=settings.alert_discord_webhook_url,
                legacy_log_posting_enabled=settings.log_posting_enabled,
                legacy_post_delay_seconds=settings.post_delay_seconds,
                legacy_critical_ping_enabled=settings.critical_ping_enabled,
                legacy_critical_ping_role_id=settings.critical_ping_role_id,
                legacy_ping_all_critical=settings.ping_all_critical,
                legacy_ping_categories=sorted(settings.ping_categories),
                tenants_bootstrap_legacy=settings.tenants_bootstrap_legacy,
            )
        except Exception:
            logger.exception("Startup failed (DB init)")
            # Do not crash the process — keep /healthz available for debugging.

    @app.on_event("shutdown")
    async def _shutdown() -> None:
        # Close webhook clients
        for client in list(app.state.webhook_clients.values()):
            try:
                await client.aclose()
            except Exception:
                pass
        app.state.webhook_clients = {}

        try:
            await app.state.db.close()
        except Exception:
            pass

    @app.get("/")
    async def root() -> Dict[str, Any]:
        return {"ok": True, "service": "screenshots-api"}

    @app.get("/healthz")
    async def healthz() -> Dict[str, Any]:
        db_ok = app.state.db.pool is not None
        tenants_count = await app.state.db.count_tenants() if db_ok else 0
        return {
            "ok": True,
            "db_ok": db_ok,
            "tenants_enabled": settings.tenants_enabled,
            "tenants_count": tenants_count,
            "legacy_webhook_configured": bool(settings.alert_discord_webhook_url),
            "ocr_engine": settings.ocr_engine,
            "environment": settings.environment,
        }

    @app.post("/extract")
    async def extract_endpoint(
        screenshot: UploadFile = File(...),
        fast: int = Query(0),
        max_w: int = Query(1400),
        x_gl_key: Optional[str] = Header(None),
        x_api_key: Optional[str] = Header(None),
    ) -> JSONResponse:
        # Auth gate (multi-tenant: any valid tenant key; legacy: shared secret if configured)
        key = _get_key(x_gl_key, x_api_key)
        await _resolve_tenant_or_401(app, settings, key)

        img_bytes = await screenshot.read()
        text = run_ocr(img_bytes, engine=settings.ocr_engine, fast=bool(fast), max_w=int(max_w))
        return JSONResponse({"ok": True, "text": text})

    @app.post("/ingest/screenshot")
    async def ingest_screenshot(
        background_tasks: BackgroundTasks,
        screenshot: UploadFile = File(...),
        server: str = Query(""),
        tribe: str = Query(""),
        post_visible: int = Query(1),
        critical_ping: int = Query(1),
        fast: int = Query(0),
        max_w: int = Query(1400),
        x_gl_key: Optional[str] = Header(None),
        x_api_key: Optional[str] = Header(None),
    ) -> JSONResponse:
        key = _get_key(x_gl_key, x_api_key)
        tenant = await _resolve_tenant_or_401(app, settings, key)

        img_bytes = await screenshot.read()
        raw_text = run_ocr(img_bytes, engine=settings.ocr_engine, fast=bool(fast), max_w=int(max_w))

        lines = [ln.strip() for ln in raw_text.splitlines() if ln.strip()]
        parsed = parse_lines(lines, server=server, tribe=tribe)
        events = [classify_event(ev) for ev in parsed.events]

        inserted = await app.state.db.insert_events(events, tenant_id=tenant.id) if app.state.db.pool is not None else events
        inserted_count = len(inserted)

        allow_post = (
            bool(post_visible)
            and settings.log_posting_enabled
            and tenant.log_posting_enabled
            and bool(tenant.webhook_url.strip())
        )

        if allow_post and inserted:
            if settings.async_posting_enabled:
                background_tasks.add_task(
                    _post_events,
                    app=app,
                    tenant=tenant,
                    events=inserted,
                    critical_ping=bool(critical_ping),
                    env=settings.environment,
                )
            else:
                await _post_events(
                    app=app,
                    tenant=tenant,
                    events=inserted,
                    critical_ping=bool(critical_ping),
                    env=settings.environment,
                )

        return JSONResponse(
            {
                "ok": True,
                "tenant": tenant.name,
                "inserted": inserted_count,
                "posted": bool(allow_post and inserted),
                "events_total": len(events),
            }
        )

    @app.post("/ingest/logline")
    async def ingest_logline(
        background_tasks: BackgroundTasks,
        logline: str = Query(..., description="One raw tribe log line"),
        server: str = Query(""),
        tribe: str = Query(""),
        post_visible: int = Query(1),
        critical_ping: int = Query(1),
        x_gl_key: Optional[str] = Header(None),
        x_api_key: Optional[str] = Header(None),
    ) -> JSONResponse:
        key = _get_key(x_gl_key, x_api_key)
        tenant = await _resolve_tenant_or_401(app, settings, key)

        lines = [logline.strip()] if logline.strip() else []
        parsed = parse_lines(lines, server=server, tribe=tribe)
        events = [classify_event(ev) for ev in parsed.events]

        inserted = await app.state.db.insert_events(events, tenant_id=tenant.id) if app.state.db.pool is not None else events
        inserted_count = len(inserted)

        allow_post = (
            bool(post_visible)
            and settings.log_posting_enabled
            and tenant.log_posting_enabled
            and bool(tenant.webhook_url.strip())
        )

        if allow_post and inserted:
            if settings.async_posting_enabled:
                background_tasks.add_task(
                    _post_events,
                    app=app,
                    tenant=tenant,
                    events=inserted,
                    critical_ping=bool(critical_ping),
                    env=settings.environment,
                )
            else:
                await _post_events(
                    app=app,
                    tenant=tenant,
                    events=inserted,
                    critical_ping=bool(critical_ping),
                    env=settings.environment,
                )

        return JSONResponse(
            {
                "ok": True,
                "tenant": tenant.name,
                "inserted": inserted_count,
                "posted": bool(allow_post and inserted),
                "events_total": len(events),
            }
        )

    return app


app = create_app()
