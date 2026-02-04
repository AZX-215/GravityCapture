# api_main.py
# FastAPI service for Gravity Capture (stage API)
# Goals:
# - OCR screenshots (ARK Tribe Log)
# - Split into one event per Tribe Log line
# - Insert into Postgres with de-dupe
# - Post to Discord via webhook with color-coded embeds
# - Ping @role only for selected CRITICAL categories (Option B)

from __future__ import annotations

import os
import asyncio
import logging
from typing import Any, Dict, Optional

from fastapi import FastAPI, File, Form, Header, HTTPException, UploadFile
from fastapi.middleware.cors import CORSMiddleware

from config import Settings
from db import Db, Tenant, hash_api_key
from discord_webhook import DiscordWebhookClient
from ocr.router import extract_text
from tribelog.parser import stitch_wrapped_lines, parse_header_lines
from tribelog.classify import classify_event
from gc_discord.interactions import router as discord_interactions_router
from gc_discord.register_commands import register_commands_if_enabled
from tribelog.selftest import run_classifier_selftest


logger = logging.getLogger("gravitycapture")


def _parse_boolish(val: Optional[str]) -> Optional[bool]:
    if val is None:
        return None
    v = str(val).strip().lower()
    if v in {"1", "true", "yes", "y", "on", "enable", "enabled"}:
        return True
    if v in {"0", "false", "no", "n", "off", "disable", "disabled"}:
        return False
    return None


async def _post_events_background(
    inserted,
    client_ping: Optional[bool],
    webhook: Optional[DiscordWebhookClient],
    settings: Settings,
    tenant: Tenant,
) -> int:
    """Post inserted events to Discord. Runs either inline or as a background task."""
    posted = 0
    if not inserted or webhook is None:
        return 0

    for ev in inserted:
        try:
            # Option B: only ping for selected categories (even if severity is CRITICAL)
            do_ping = (
                tenant.critical_ping_enabled
                and ev.severity == "CRITICAL"
                and (tenant.ping_all_critical or ev.category in tenant.ping_categories)
            )
            if client_ping is False:
                do_ping = False

            await webhook.post_event_from_parsed(
                ev,
                mention_role_id=(tenant.critical_ping_role_id or settings.critical_ping_role_id),
                mention=do_ping,
                env=settings.environment,
            )
            posted += 1

            delay = float(tenant.post_delay_seconds or 0.0)
            if delay > 0:
                await asyncio.sleep(delay)
        except Exception as e:
            logger.exception("Discord posting failed: %s", e)

    return posted


def _require_key(settings: Settings, x_gl_key: Optional[str], x_api_key: Optional[str]) -> str:
    """Legacy single-tenant auth."""
    # If no secret is configured, allow requests (useful for local dev).
    if not settings.gl_shared_secret:
        return (x_gl_key or x_api_key or "").strip()
    key = (x_gl_key or x_api_key or "").strip()
    if not key or key != settings.gl_shared_secret:
        raise HTTPException(status_code=401, detail="Unauthorized")
    return key


def create_app() -> FastAPI:
    settings = Settings.from_env()

    app = FastAPI(title="Gravity Capture Stage API", version="2.1")

    app.add_middleware(
        CORSMiddleware,
        allow_origins=["*"],
        allow_credentials=False,
        allow_methods=["*"],
        allow_headers=["*"],
    )

    # Set DISCORD_PUBLIC_KEY on Railway to enable verification.
    # Discord slash-command interactions endpoint (optional)
    app.include_router(discord_interactions_router)
    # --- state ---
    app.state.settings = settings
    app.state.db = Db(settings.database_url)
    app.state.webhook_clients: Dict[str, DiscordWebhookClient] = {}
    app.state.legacy_tenant_id: Optional[int] = None

    def _get_webhook_client(url: str) -> Optional[DiscordWebhookClient]:
        u = (url or "").strip()
        if not u:
            return None
        c = app.state.webhook_clients.get(u)
        if c is None:
            c = DiscordWebhookClient(u)
            app.state.webhook_clients[u] = c
        return c

    def _legacy_tenant() -> Tenant:
        # Single-tenant defaults stored in env.
        return Tenant(
            id=int(app.state.legacy_tenant_id or 0),
            name=settings.legacy_tenant_name,
            api_key_hash=hash_api_key(settings.gl_shared_secret or ""),
            webhook_url=settings.alert_discord_webhook_url,
            is_enabled=True,
            log_posting_enabled=settings.log_posting_enabled,
            post_delay_seconds=settings.post_delay_seconds,
            critical_ping_enabled=settings.critical_ping_enabled,
            critical_ping_role_id=settings.critical_ping_role_id,
            ping_all_critical=settings.ping_all_critical,
            ping_categories=set(settings.ping_categories),
        )

    async def _resolve_tenant(x_gl_key: Optional[str], x_api_key: Optional[str]) -> Tenant:
        if settings.tenants_enabled:
            key = (x_gl_key or x_api_key or "").strip()
            if not key:
                raise HTTPException(status_code=401, detail="Unauthorized")

            if not settings.database_url:
                # Without DB we cannot resolve tenants; fall back only if it's the legacy secret.
                if settings.gl_shared_secret and key == settings.gl_shared_secret:
                    return _legacy_tenant()
                raise HTTPException(status_code=500, detail="DATABASE_URL not set")

            tenant = await app.state.db.resolve_tenant_by_key(key)
            if tenant is None and settings.tenants_bootstrap_legacy and settings.gl_shared_secret and key == settings.gl_shared_secret:
                # Bootstrap the legacy tenant from env and retry.
                try:
                    legacy_id = await app.state.db.ensure_legacy_tenant(
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
                    app.state.legacy_tenant_id = legacy_id
                except Exception as e:
                    logger.exception("Legacy tenant bootstrap failed: %s", e)
                tenant = await app.state.db.resolve_tenant_by_key(key)

            if tenant is None or not tenant.is_enabled:
                raise HTTPException(status_code=401, detail="Unauthorized")
            return tenant

        # Legacy single-tenant mode:
        _require_key(settings, x_gl_key, x_api_key)
        return _legacy_tenant()

    @app.on_event("startup")
    async def _startup() -> None:
        # DB
        await app.state.db.start()

        # Classifier self-test (optional).
        # Set CLASSIFIER_SELFTEST=1 to run on startup and catch missing regex/constants immediately.
        if str(os.getenv("CLASSIFIER_SELFTEST", "0")).strip() in {"1", "true", "yes", "on"}:
            try:
                run_classifier_selftest()
            except Exception as e:
                logger.exception("Classifier self-test failed: %s", e)
                # If strict, fail fast so Railway shows the error during startup instead of random 500s later.
                if str(os.getenv("CLASSIFIER_SELFTEST_STRICT", "1")).strip() in {"1", "true", "yes", "on"}:
                    raise


        # Always bootstrap a legacy tenant when DB is available.
        # This keeps old installs working and also backfills pre-tenant rows.
        
        # Discord slash-command auto-registration (optional).
        # Set DISCORD_AUTO_REGISTER=1 and provide DISCORD_BOT_TOKEN + DISCORD_APPLICATION_ID.
        # If DISCORD_GUILD_ID is set, commands are registered to that guild for immediate availability.
        try:
            asyncio.create_task(register_commands_if_enabled())
        except Exception as e:
            logger.exception("Failed to schedule Discord auto-register task: %s", e)

        if settings.database_url and settings.tenants_bootstrap_legacy:
            legacy_secret = settings.gl_shared_secret or "legacy-no-secret"
            try:
                legacy_id = await app.state.db.ensure_legacy_tenant(
                    legacy_secret=legacy_secret,
                    legacy_tenant_name=settings.legacy_tenant_name,
                    legacy_webhook_url=settings.alert_discord_webhook_url,
                    legacy_log_posting_enabled=settings.log_posting_enabled,
                    legacy_post_delay_seconds=settings.post_delay_seconds,
                    legacy_critical_ping_enabled=settings.critical_ping_enabled,
                    legacy_critical_ping_role_id=settings.critical_ping_role_id,
                    legacy_ping_all_critical=settings.ping_all_critical,
                    legacy_ping_categories=sorted(settings.ping_categories),
                )
                app.state.legacy_tenant_id = legacy_id
            except Exception as e:
                logger.exception("Legacy tenant bootstrap failed at startup: %s", e)

    @app.on_event("shutdown")
    async def _shutdown() -> None:
        # Close webhook clients
        for c in list(app.state.webhook_clients.values()):
            try:
                await c.aclose()
            except Exception:
                pass
        app.state.webhook_clients.clear()

        # Close DB
        await app.state.db.close()

    # ---- Health ----
    @app.get("/")
    @app.get("/healthz")
    async def healthz() -> Dict[str, Any]:
        ok = True
        notes = []
        if settings.tenants_enabled and not settings.database_url:
            ok = False
            notes.append("DATABASE_URL missing (tenants mode requires DB)")
        if not settings.tenants_enabled and settings.gl_shared_secret and not settings.alert_discord_webhook_url:
            notes.append("ALERT_DISCORD_WEBHOOK_URL not set (posting will be disabled)")
        return {
            "ok": ok,
            "env": settings.environment,
            "tenants_enabled": settings.tenants_enabled,
            "legacy_tenant_id": app.state.legacy_tenant_id,
            "db": bool(settings.database_url),
            "webhook_default_set": bool(settings.alert_discord_webhook_url),
            "notes": notes,
        }

    # ---- OCR-only debug endpoint (no DB insert, no Discord post) ----
    @app.post("/extract")
    async def extract_endpoint(
        file: Optional[UploadFile] = File(default=None),
        image: Optional[UploadFile] = File(default=None),
        x_gl_key: Optional[str] = Header(default=None, alias="X-GL-Key"),
        x_api_key: Optional[str] = Header(default=None, alias="x-api-key"),
        engine: Optional[str] = None,
        engine_form: Optional[str] = Form("auto"),
        fast: Optional[bool] = None,
        fast_form: Optional[str] = Form(default=None),
        max_w: Optional[int] = None,
        max_w_form: Optional[str] = Form(default=None),
    ) -> Dict[str, Any]:
        # Auth
        if settings.tenants_enabled:
            await _resolve_tenant(x_gl_key, x_api_key)
        else:
            _require_key(settings, x_gl_key, x_api_key)

        if file is None and image is None:
            raise HTTPException(status_code=422, detail="missing file")
        up = file or image
        img_bytes = await up.read()

        # Normalize query/form inputs
        eng = (engine or engine_form or "auto").strip().lower()
        fast_val = fast
        if fast_val is None:
            fast_val = _parse_boolish(fast_form)
        if fast_val is None:
            fast_val = False

        mw = max_w
        if mw is None and max_w_form:
            try:
                mw = int(str(max_w_form).strip())
            except Exception:
                mw = None

        ocr = extract_text(img_bytes, engine_hint=eng, fast=bool(fast_val), max_w=mw)
        raw_lines = [str(x).strip() for x in (ocr.get("lines_text") or []) if str(x).strip()]
        stitched = stitch_wrapped_lines(raw_lines)
        header_lines = parse_header_lines(stitched)

        return {
            "ok": True,
            "engine": ocr.get("engine"),
            "variant": ocr.get("variant"),
            "conf": ocr.get("conf"),
            "lines_text": raw_lines,
            "stitched": stitched,
            "headers": header_lines,
        }

    @app.post("/api/ocr/extract")
    async def api_ocr_extract_alias(
        file: Optional[UploadFile] = File(default=None),
        image: Optional[UploadFile] = File(default=None),
        x_gl_key: Optional[str] = Header(default=None, alias="X-GL-Key"),
        x_api_key: Optional[str] = Header(default=None, alias="x-api-key"),
        engine: str = Form("auto"),
    ) -> Dict[str, Any]:
        return await extract_endpoint(file=file, image=image, x_gl_key=x_gl_key, x_api_key=x_api_key, engine=engine)

    # ---- Ingest endpoints (insert+post) ----
    async def _ingest_screenshot_impl(
        file: Optional[UploadFile],
        server: str,
        tribe: str,
        post_visible: str,
        x_gl_key: Optional[str],
        x_api_key: Optional[str],
        critical_ping: Optional[str],
        x_client_critical_ping: Optional[str],
    ) -> Dict[str, Any]:
        tenant = await _resolve_tenant(x_gl_key, x_api_key)

        if not settings.database_url:
            raise HTTPException(status_code=500, detail="DATABASE_URL not set")

        if file is None:
            raise HTTPException(status_code=422, detail="missing file")
        img_bytes = await file.read()

        # Keep request latency low for the desktop client: use fast OCR path by default.
        ocr = extract_text(img_bytes, engine_hint=settings.ocr_engine, fast=True)

        # Prefer the line-wise output for event splitting.
        raw_lines = [str(x).strip() for x in (ocr.get("lines_text") or []) if str(x).strip()]
        stitched = stitch_wrapped_lines(raw_lines)
        header_lines = parse_header_lines(stitched)

        events = []
        for h in header_lines:
            ev = classify_event(
                server=server or "unknown",
                tribe=tribe or "unknown",
                ark_day=h["ark_day"],
                ark_time=h["ark_time"],
                message=h["message"],
                raw_line=h["raw_line"],
            )
            events.append(ev)

        inserted = await app.state.db.insert_events(events, tenant_id=tenant.id)

        client_ping = _parse_boolish(critical_ping)
        if client_ping is None:
            client_ping = _parse_boolish(x_client_critical_ping)

        posted = 0
        enqueued_events = 0
        posting_mode = "off"

        allow_post = _parse_boolish(post_visible) is True

        webhook_url = (tenant.webhook_url or settings.alert_discord_webhook_url).strip()
        webhook = _get_webhook_client(webhook_url)

        if allow_post and tenant.log_posting_enabled and webhook is not None and inserted:
            if settings.async_posting_enabled:
                posting_mode = "async"
                enqueued_events = len(inserted)

                async def _runner():
                    try:
                        await _post_events_background(inserted, client_ping, webhook, settings, tenant)
                    except Exception as e:
                        logger.exception("Background posting task crashed: %s", e)

                asyncio.create_task(_runner())
            else:
                posting_mode = "sync"
                posted = await _post_events_background(inserted, client_ping, webhook, settings, tenant)

        return {
            "ok": True,
            "tenant": tenant.name,
            "server": server,
            "tribe": tribe,
            "engine": ocr.get("engine"),
            "variant": ocr.get("variant"),
            "ocr_conf": ocr.get("conf"),
            "total_events": len(events),
            "inserted_events": len(inserted),
            "posted_events": posted,
            "posting_mode": posting_mode,
            "enqueued_events": enqueued_events,
            "post_visible": str(post_visible or "0"),
        }

    @app.post("/ingest/screenshot")
    async def ingest_screenshot(
        file: Optional[UploadFile] = File(default=None),
        image: Optional[UploadFile] = File(default=None),
        server: str = Form("unknown"),
        tribe: str = Form("unknown"),
        post_visible: str = Form("1"),
        critical_ping: Optional[str] = Form(default=None),
        x_client_critical_ping: Optional[str] = Header(default=None, alias="X-Client-Critical-Ping"),
        x_gl_key: Optional[str] = Header(default=None, alias="X-GL-Key"),
        x_api_key: Optional[str] = Header(default=None, alias="x-api-key"),
    ) -> Dict[str, Any]:
        return await _ingest_screenshot_impl(file or image, server, tribe, post_visible, x_gl_key, x_api_key, critical_ping, x_client_critical_ping)

    @app.post("/api/ingest/screenshot")
    async def ingest_screenshot_alias(
        file: Optional[UploadFile] = File(default=None),
        image: Optional[UploadFile] = File(default=None),
        server: str = Form("unknown"),
        tribe: str = Form("unknown"),
        post_visible: str = Form("1"),
        critical_ping: Optional[str] = Form(default=None),
        x_client_critical_ping: Optional[str] = Header(default=None, alias="X-Client-Critical-Ping"),
        x_gl_key: Optional[str] = Header(default=None, alias="X-GL-Key"),
        x_api_key: Optional[str] = Header(default=None, alias="x-api-key"),
    ) -> Dict[str, Any]:
        return await _ingest_screenshot_impl(file or image, server, tribe, post_visible, x_gl_key, x_api_key, critical_ping, x_client_critical_ping)

    @app.post("/ingest/log-line")
    async def ingest_log_line(
        line: str = Form(...),
        server: str = Form("unknown"),
        tribe: str = Form("unknown"),
        post_visible: str = Form("1"),
        critical_ping: Optional[str] = Form(default=None),
        x_client_critical_ping: Optional[str] = Header(default=None, alias="X-Client-Critical-Ping"),
        x_gl_key: Optional[str] = Header(default=None, alias="X-GL-Key"),
        x_api_key: Optional[str] = Header(default=None, alias="x-api-key"),
    ) -> Dict[str, Any]:
        tenant = await _resolve_tenant(x_gl_key, x_api_key)

        if not settings.database_url:
            raise HTTPException(status_code=500, detail="DATABASE_URL not set")

        stitched = stitch_wrapped_lines([line])
        header_lines = parse_header_lines(stitched)
        if not header_lines:
            return {"ok": False, "error": "no_header"}

        events = []
        for h in header_lines:
            events.append(
                classify_event(
                    server=server or "unknown",
                    tribe=tribe or "unknown",
                    ark_day=h["ark_day"],
                    ark_time=h["ark_time"],
                    message=h["message"],
                    raw_line=h["raw_line"],
                )
            )

        inserted = await app.state.db.insert_events(events, tenant_id=tenant.id)

        client_ping = _parse_boolish(critical_ping)
        if client_ping is None:
            client_ping = _parse_boolish(x_client_critical_ping)

        posted = 0
        enqueued_events = 0
        posting_mode = "off"

        allow_post = _parse_boolish(post_visible) is True

        webhook_url = (tenant.webhook_url or settings.alert_discord_webhook_url).strip()
        webhook = _get_webhook_client(webhook_url)

        if allow_post and tenant.log_posting_enabled and webhook is not None and inserted:
            if settings.async_posting_enabled:
                posting_mode = "async"
                enqueued_events = len(inserted)

                async def _runner():
                    try:
                        await _post_events_background(inserted, client_ping, webhook, settings, tenant)
                    except Exception as e:
                        logger.exception("Background posting task crashed: %s", e)

                asyncio.create_task(_runner())
            else:
                posting_mode = "sync"
                posted = await _post_events_background(inserted, client_ping, webhook, settings, tenant)

        return {
            "ok": True,
            "tenant": tenant.name,
            "server": server,
            "tribe": tribe,
            "total_events": len(events),
            "inserted_events": len(inserted),
            "posted_events": posted,
            "posting_mode": posting_mode,
            "enqueued_events": enqueued_events,
            "post_visible": str(post_visible or "0"),
        }

    @app.post("/api/ingest/log-line")
    async def ingest_log_line_alias(
        line: str = Form(...),
        server: str = Form("unknown"),
        tribe: str = Form("unknown"),
        x_gl_key: Optional[str] = Header(default=None, alias="X-GL-Key"),
        x_api_key: Optional[str] = Header(default=None, alias="x-api-key"),
    ) -> Dict[str, Any]:
        return await ingest_log_line(line=line, server=server, tribe=tribe, x_gl_key=x_gl_key, x_api_key=x_api_key)

    return app


app = create_app()