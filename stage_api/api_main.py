from __future__ import annotations

import asyncio
import logging
from typing import Dict, Optional, Tuple

from fastapi import FastAPI, File, Form, Header, HTTPException, UploadFile

from config import Settings
from db import Db, Tenant
from discord_webhook import DiscordWebhookClient
from ocr import extract_text
from tribelog.classify import classify_event
from tribelog.parser import parse_header_lines, stitch_wrapped_lines
from tribelog.models import ParsedEvent


log = logging.getLogger("gravity_capture_api")


def _ok(**fields):
    return {"ok": True, **fields}


def _pick_key(x_gl_key: Optional[str], x_api_key: Optional[str]) -> str:
    return (x_gl_key or x_api_key or "").strip()


def _require_legacy_key(shared_secret: str, provided: str) -> None:
    if not shared_secret:
        raise HTTPException(status_code=500, detail="Server not configured: GL_SHARED_SECRET is missing")
    if not provided:
        raise HTTPException(status_code=401, detail="Missing API key")
    if provided != shared_secret:
        raise HTTPException(status_code=401, detail="Invalid API key")


async def _resolve_tenant_for_request(
    *,
    settings: Settings,
    db: Optional[Db],
    x_gl_key: Optional[str],
    x_api_key: Optional[str],
    require_db_lookup: bool,
) -> Tuple[str, Optional[Tenant]]:
    """Returns (provided_key, tenant_or_none).

    - In TENANTS_ENABLED mode: key must exist in tenants table.
    - In legacy mode: key must match GL_SHARED_SECRET.

    For endpoints that do not hit the DB (e.g., /extract), set require_db_lookup=False.
    """

    provided = _pick_key(x_gl_key, x_api_key)

    if settings.tenants_enabled:
        if not provided:
            raise HTTPException(status_code=401, detail="Missing API key")
        if require_db_lookup:
            if db is None or db.default_tenant_id is None:
                # default_tenant_id is set when DB is started
                raise HTTPException(status_code=500, detail="DB not ready")
            tenant = await db.resolve_tenant_by_key(provided)
            if tenant is None or not tenant.is_enabled:
                raise HTTPException(status_code=401, detail="Invalid API key")
            return provided, tenant

        # /extract path: allow legacy shared secret as a fallback if DB isn't available.
        if settings.gl_shared_secret and provided == settings.gl_shared_secret:
            return provided, None
        if db is not None and db.is_ready and db.default_tenant_id is not None:
            tenant = await db.resolve_tenant_by_key(provided)
            if tenant is None or not tenant.is_enabled:
                raise HTTPException(status_code=401, detail="Invalid API key")
            return provided, tenant
        raise HTTPException(status_code=401, detail="Invalid API key")

    # Legacy/single-tenant mode
    _require_legacy_key(settings.gl_shared_secret, provided)
    if db is not None and db.is_ready and db.default_tenant_id is not None:
        tenant = await db.resolve_tenant_by_key(provided)
        return provided, tenant
    return provided, None


def _get_webhook_client(app: FastAPI, tenant: Tenant) -> Optional[DiscordWebhookClient]:
    if not tenant.webhook_url:
        return None

    cache: Dict[int, Tuple[str, DiscordWebhookClient]] = app.state.webhook_clients
    cached = cache.get(tenant.id)
    if cached and cached[0] == tenant.webhook_url:
        return cached[1]

    # Recreate on URL change
    client = DiscordWebhookClient(tenant.webhook_url, post_delay_seconds=tenant.post_delay_seconds)
    cache[tenant.id] = (tenant.webhook_url, client)
    return client


async def _maybe_post_event(
    *,
    event: ParsedEvent,
    tenant: Tenant,
    webhook: DiscordWebhookClient,
    client_ping_enabled: bool,
) -> bool:
    if event is None:
        return False

    # Optional ping for CRITICAL events
    content = None
    if (
        client_ping_enabled
        and tenant.critical_ping_enabled
        and (event.severity or "").upper() == "CRITICAL"
    ):
        if tenant.ping_all_critical or (event.category in tenant.ping_categories):
            if tenant.critical_ping_role_id:
                content = f"<@&{tenant.critical_ping_role_id}>"

    title = f"[{event.severity}] {event.category}"
    description = event.message

    embed = {
        "title": title,
        "description": description,
        "fields": [
            {"name": "Server", "value": event.server or "-", "inline": True},
            {"name": "Tribe", "value": event.tribe or "-", "inline": True},
            {"name": "Day", "value": str(event.ark_day), "inline": True},
            {"name": "Time", "value": event.ark_time or "-", "inline": True},
            {"name": "Actor", "value": event.actor or "-", "inline": False},
        ],
        "footer": {"text": event.event_hash},
    }

    ok = await webhook.send_embed(embed=embed, content=content)
    return ok


async def _post_events(
    *,
    app: FastAPI,
    tenant: Tenant,
    events: list[ParsedEvent],
    client_ping_enabled: bool,
) -> int:
    if not events:
        return 0

    if not tenant.log_posting_enabled:
        return 0

    webhook = _get_webhook_client(app, tenant)
    if webhook is None:
        return 0

    posted = 0
    for ev in events:
        try:
            if await _maybe_post_event(
                event=ev,
                tenant=tenant,
                webhook=webhook,
                client_ping_enabled=client_ping_enabled,
            ):
                posted += 1
        except Exception:
            log.exception("Discord post failed")
    return posted


def create_app() -> FastAPI:
    logging.basicConfig(level=logging.INFO)

    settings = Settings.from_env()

    app = FastAPI(title="GravityCapture Stage API", version="0.0.0")
    app.state.settings = settings
    app.state.db = Db()
    app.state.webhook_clients = {}  # tenant_id -> (webhook_url, DiscordWebhookClient)

    @app.on_event("startup")
    async def _startup() -> None:
        # DB is required for /ingest. /extract can work without DB.
        if settings.database_url:
            await app.state.db.start(
                settings.database_url,
                legacy_tenant_name=settings.legacy_tenant_name,
                legacy_key=settings.gl_shared_secret,
                legacy_webhook_url=settings.alert_discord_webhook_url,
                legacy_log_posting_enabled=settings.log_posting_enabled,
                legacy_post_delay_seconds=settings.post_delay_seconds,
                legacy_critical_ping_enabled=settings.critical_ping_enabled,
                legacy_critical_ping_role_id=settings.critical_ping_role_id,
                legacy_ping_all_critical=settings.ping_all_critical,
                legacy_ping_categories=settings.ping_categories,
                legacy_is_enabled=True,
            )
            log.info("DB connected")
        else:
            log.warning("DATABASE_URL is not set; /ingest endpoints will fail")

    @app.on_event("shutdown")
    async def _shutdown() -> None:
        # Close webhook clients
        for _, client in list(app.state.webhook_clients.values()):
            try:
                await client.close()
            except Exception:
                pass
        app.state.webhook_clients.clear()

        # Close DB
        try:
            await app.state.db.close()
        except Exception:
            pass

    @app.get("/healthz")
    async def healthz() -> dict:
        db_ok = False
        tenant_count = None
        default_tenant_id = None

        try:
            if app.state.db._pool is not None:
                async with app.state.db._pool.acquire() as conn:
                    await conn.execute("SELECT 1")
                db_ok = True
                tenant_count = await app.state.db.count_tenants()
                default_tenant_id = app.state.db.default_tenant_id
        except Exception:
            db_ok = False

        return _ok(
            environment=settings.environment,
            ocr_engine=settings.ocr_engine,
            tenants_enabled=settings.tenants_enabled,
            tenant_count=tenant_count,
            default_tenant_id=default_tenant_id,
            db_ok=db_ok,
            legacy_webhook_configured=bool(settings.alert_discord_webhook_url),
            global_posting_enabled=settings.log_posting_enabled,
        )

    @app.post("/extract")
    async def extract_endpoint(
        file: UploadFile = File(...),
        engine: str = Form(""),
        fast: int = Form(0),
        max_w: int = Form(1400),
        x_gl_key: str | None = Header(default=None, alias="X-GL-Key"),
        x_api_key: str | None = Header(default=None, alias="x-api-key"),
    ) -> dict:
        # Auth only; no DB writes
        await _resolve_tenant_for_request(
            settings=settings,
            db=app.state.db,
            x_gl_key=x_gl_key,
            x_api_key=x_api_key,
            require_db_lookup=False,
        )

        image_bytes = await file.read()
        ocr_engine = (engine or settings.ocr_engine or "auto").strip().lower()
        result = extract_text(image_bytes, engine=ocr_engine, fast=bool(fast), max_w=int(max_w))

        return _ok(
            engine=ocr_engine,
            lines_text=result.get("lines_text", []),
            debug=result.get("debug", {}),
        )

    @app.post("/ingest/screenshot")
    async def ingest_screenshot(
        file: UploadFile = File(...),
        server: str = Form(""),
        tribe: str = Form(""),
        client_ping_enabled: int = Form(1),
        engine: str = Form(""),
        fast: int = Form(0),
        max_w: int = Form(1400),
        x_gl_key: str | None = Header(default=None, alias="X-GL-Key"),
        x_api_key: str | None = Header(default=None, alias="x-api-key"),
    ) -> dict:
        if not settings.database_url:
            raise HTTPException(status_code=500, detail="DATABASE_URL is not set")

        _key, tenant = await _resolve_tenant_for_request(
            settings=settings,
            db=app.state.db,
            x_gl_key=x_gl_key,
            x_api_key=x_api_key,
            require_db_lookup=True,
        )
        if tenant is None:
            # Should not happen when require_db_lookup=True
            raise HTTPException(status_code=401, detail="Invalid API key")

        image_bytes = await file.read()
        ocr_engine = (engine or settings.ocr_engine or "auto").strip().lower()

        ocr = extract_text(image_bytes, engine=ocr_engine, fast=bool(fast), max_w=int(max_w))
        lines_text = ocr.get("lines_text", [])

        stitched = stitch_wrapped_lines(lines_text)
        parsed_lines = parse_header_lines(stitched)

        classified = []
        for pl in parsed_lines:
            ce = classify_event(pl)
            if ce is None:
                continue
            classified.append(ce)

        parsed_events: list[ParsedEvent] = []
        for ce in classified:
            # Ensure server/tribe are always set from client
            e = ParsedEvent(
                server=server.strip(),
                tribe=tribe.strip(),
                ark_day=ce.ark_day,
                ark_time=ce.ark_time,
                severity=ce.severity,
                category=ce.category,
                actor=ce.actor,
                message=ce.message,
                raw_line=ce.raw_line,
                event_hash="",  # set below
            )
            parsed_events.append(e)

        # Compute hash + insert (dedupe)
        for i, ev in enumerate(parsed_events):
            parsed_events[i] = ParsedEvent(
                **{
                    **ev.__dict__,
                    "event_hash": app.state.db.compute_event_hash(ev),
                }
            )

        inserted = await app.state.db.insert_events(parsed_events, tenant_id=tenant.id)

        posted = 0
        if settings.log_posting_enabled and inserted:
            if settings.async_posting_enabled:
                asyncio.create_task(
                    _post_events(
                        app=app,
                        tenant=tenant,
                        events=inserted,
                        client_ping_enabled=bool(client_ping_enabled),
                    )
                )
                posted = -1
            else:
                posted = await _post_events(
                    app=app,
                    tenant=tenant,
                    events=inserted,
                    client_ping_enabled=bool(client_ping_enabled),
                )

        return _ok(
            tenant_id=tenant.id,
            tenant_name=tenant.name,
            engine=ocr_engine,
            extracted_line_count=len(lines_text),
            stitched_line_count=len(stitched),
            parsed_line_count=len(parsed_lines),
            classified_count=len(classified),
            inserted_count=len(inserted),
            posted_count=posted,
            debug=ocr.get("debug", {}),
        )

    @app.post("/api/ingest/log-line")
    async def ingest_log_line(
        line: str = Form(""),
        server: str = Form(""),
        tribe: str = Form(""),
        client_ping_enabled: int = Form(1),
        x_gl_key: str | None = Header(default=None, alias="X-GL-Key"),
        x_api_key: str | None = Header(default=None, alias="x-api-key"),
    ) -> dict:
        if not settings.database_url:
            raise HTTPException(status_code=500, detail="DATABASE_URL is not set")

        _key, tenant = await _resolve_tenant_for_request(
            settings=settings,
            db=app.state.db,
            x_gl_key=x_gl_key,
            x_api_key=x_api_key,
            require_db_lookup=True,
        )
        if tenant is None:
            raise HTTPException(status_code=401, detail="Invalid API key")

        lines_text = [line] if line else []
        stitched = stitch_wrapped_lines(lines_text)
        parsed_lines = parse_header_lines(stitched)

        classified = []
        for pl in parsed_lines:
            ce = classify_event(pl)
            if ce is None:
                continue
            classified.append(ce)

        parsed_events: list[ParsedEvent] = []
        for ce in classified:
            e = ParsedEvent(
                server=server.strip(),
                tribe=tribe.strip(),
                ark_day=ce.ark_day,
                ark_time=ce.ark_time,
                severity=ce.severity,
                category=ce.category,
                actor=ce.actor,
                message=ce.message,
                raw_line=ce.raw_line,
                event_hash="",
            )
            parsed_events.append(e)

        for i, ev in enumerate(parsed_events):
            parsed_events[i] = ParsedEvent(
                **{
                    **ev.__dict__,
                    "event_hash": app.state.db.compute_event_hash(ev),
                }
            )

        inserted = await app.state.db.insert_events(parsed_events, tenant_id=tenant.id)

        posted = 0
        if settings.log_posting_enabled and inserted:
            if settings.async_posting_enabled:
                asyncio.create_task(
                    _post_events(
                        app=app,
                        tenant=tenant,
                        events=inserted,
                        client_ping_enabled=bool(client_ping_enabled),
                    )
                )
                posted = -1
            else:
                posted = await _post_events(
                    app=app,
                    tenant=tenant,
                    events=inserted,
                    client_ping_enabled=bool(client_ping_enabled),
                )

        return _ok(
            tenant_id=tenant.id,
            inserted_count=len(inserted),
            posted_count=posted,
        )

    return app


app = create_app()
