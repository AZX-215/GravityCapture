# api_main.py
# FastAPI service for Gravity Capture (stage API)
# Goals:
# - OCR screenshots (ARK Tribe Log)
# - Split into one event per Tribe Log line
# - Insert into Postgres with de-dupe
# - Post to Discord via webhook with color-coded embeds
# - Ping @Gravity OPS only for selected CRITICAL categories (Option B)

from __future__ import annotations

import asyncio
from typing import Any, Dict, Optional

from fastapi import FastAPI, File, Form, Header, HTTPException, UploadFile
from fastapi.middleware.cors import CORSMiddleware

from config import Settings
from db import Db
from discord_webhook import DiscordWebhookClient
from ocr.router import extract_text
from tribelog.parser import stitch_wrapped_lines, parse_header_lines
from tribelog.classify import classify_event



def _parse_boolish(val: Optional[str]) -> Optional[bool]:
    if val is None:
        return None
    v = str(val).strip().lower()
    if v in {"1", "true", "yes", "y", "on", "enable", "enabled"}:
        return True
    if v in {"0", "false", "no", "n", "off", "disable", "disabled"}:
        return False
    return None

def _require_key(settings: Settings, x_gl_key: Optional[str], x_api_key: Optional[str]) -> None:
    # If no secret is configured, allow requests (useful for local dev).
    if not settings.gl_shared_secret:
        return
    key = (x_gl_key or x_api_key or "").strip()
    if not key or key != settings.gl_shared_secret:
        raise HTTPException(status_code=401, detail="Unauthorized")


def create_app() -> FastAPI:
    settings = Settings.from_env()

    app = FastAPI(title="Gravity Capture Stage API", version="2.0")

    app.add_middleware(
        CORSMiddleware,
        allow_origins=["*"],
        allow_credentials=False,
        allow_methods=["*"],
        allow_headers=["*"],
    )

    app.state.settings = settings
    app.state.db = Db(dsn=settings.database_url)
    app.state.webhook = DiscordWebhookClient(
        webhook_url=settings.alert_discord_webhook_url,
        post_delay_seconds=settings.post_delay_seconds,
    ) if settings.alert_discord_webhook_url else None

    @app.on_event("startup")
    async def _startup() -> None:
        if not settings.database_url:
            # allow running /extract without DB
            return
        await app.state.db.start()

    @app.on_event("shutdown")
    async def _shutdown() -> None:
        try:
            if app.state.webhook is not None:
                await app.state.webhook.aclose()
        finally:
            await app.state.db.close()

    @app.get("/")
    async def root() -> Dict[str, Any]:
        return {"status": "ok", "service": "gravity-capture-stage-api", "env": settings.environment}

    @app.get("/healthz")
    async def healthz() -> Dict[str, Any]:
        return {
            "status": "ok",
            "env": settings.environment,
            "db": "configured" if bool(settings.database_url) else "missing",
            "webhook": "configured" if bool(settings.alert_discord_webhook_url) else "missing",
            "ocr_engine": settings.ocr_engine,
        }

    # ---- OCR-only endpoints (desktop app uses these for preview) ----
    @app.post("/extract")
    async def extract_endpoint(
        file: Optional[UploadFile] = File(default=None),
        image: Optional[UploadFile] = File(default=None),
        x_gl_key: Optional[str] = Header(default=None, alias="X-GL-Key"),
        x_api_key: Optional[str] = Header(default=None, alias="x-api-key"),
        engine: str = Form("auto"),
    ) -> Dict[str, Any]:
        _require_key(settings, x_gl_key, x_api_key)
        up = file or image
        if up is None:
            raise HTTPException(status_code=422, detail="missing file")
        img_bytes = await up.read()
        return extract_text(img_bytes, engine_hint=(engine or settings.ocr_engine))

    @app.post("/api/extract")
    async def extract_endpoint_alias(
        file: Optional[UploadFile] = File(default=None),
        image: Optional[UploadFile] = File(default=None),
        x_gl_key: Optional[str] = Header(default=None, alias="X-GL-Key"),
        x_api_key: Optional[str] = Header(default=None, alias="x-api-key"),
        engine: str = Form("auto"),
    ) -> Dict[str, Any]:
        return await extract_endpoint(file=file, image=image, x_gl_key=x_gl_key, x_api_key=x_api_key, engine=engine)

    @app.post("/ocr")
    async def ocr_endpoint(
        file: Optional[UploadFile] = File(default=None),
        image: Optional[UploadFile] = File(default=None),
        x_gl_key: Optional[str] = Header(default=None, alias="X-GL-Key"),
        x_api_key: Optional[str] = Header(default=None, alias="x-api-key"),
        engine: str = Form("auto"),
    ) -> Dict[str, Any]:
        return await extract_endpoint(file=file, image=image, x_gl_key=x_gl_key, x_api_key=x_api_key, engine=engine)

    @app.post("/ocr/extract")
    async def ocr_extract_alias(
        file: Optional[UploadFile] = File(default=None),
        image: Optional[UploadFile] = File(default=None),
        x_gl_key: Optional[str] = Header(default=None, alias="X-GL-Key"),
        x_api_key: Optional[str] = Header(default=None, alias="x-api-key"),
        engine: str = Form("auto"),
    ) -> Dict[str, Any]:
        return await extract_endpoint(file=file, image=image, x_gl_key=x_gl_key, x_api_key=x_api_key, engine=engine)

    @app.post("/api/ocr")
    async def api_ocr_alias(
        file: Optional[UploadFile] = File(default=None),
        image: Optional[UploadFile] = File(default=None),
        x_gl_key: Optional[str] = Header(default=None, alias="X-GL-Key"),
        x_api_key: Optional[str] = Header(default=None, alias="x-api-key"),
        engine: str = Form("auto"),
    ) -> Dict[str, Any]:
        return await extract_endpoint(file=file, image=image, x_gl_key=x_gl_key, x_api_key=x_api_key, engine=engine)

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
        _require_key(settings, x_gl_key, x_api_key)

        if not settings.database_url:
            raise HTTPException(status_code=500, detail="DATABASE_URL not set")

        if file is None:
            raise HTTPException(status_code=422, detail="missing file")
        img_bytes = await file.read()
        ocr = extract_text(img_bytes, engine_hint=settings.ocr_engine)

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

        inserted = await app.state.db.insert_events(events)

        client_ping = _parse_boolish(critical_ping)
        if client_ping is None:
            client_ping = _parse_boolish(x_client_critical_ping)

        posted = 0
        if settings.log_posting_enabled and app.state.webhook is not None:
            for ev in inserted:
                # Option B: only ping for selected categories (even if severity is CRITICAL)
                do_ping = (
                    settings.critical_ping_enabled
                    and ev.severity == "CRITICAL"
                    and (settings.ping_all_critical or ev.category in settings.ping_categories)
                )
                if client_ping is False:
                    do_ping = False
                await app.state.webhook.post_event_from_parsed(
                    ev,
                    mention_role_id=settings.critical_ping_role_id,
                    mention=do_ping,
                    env=settings.environment,
                )
                posted += 1
                if settings.post_delay_seconds > 0:
                    await asyncio.sleep(settings.post_delay_seconds)

        return {
            "ok": True,
            "engine": ocr.get("engine"),
            "variant": ocr.get("variant"),
            "ocr_conf": ocr.get("conf"),
            "total_events": len(events),
            "inserted_events": len(inserted),
            "posted_events": posted,
            "post_visible": str(post_visible or "0"),
        }

    @app.post("/ingest/screenshot")
    async def ingest_screenshot(
        file: Optional[UploadFile] = File(default=None),
        image: Optional[UploadFile] = File(default=None),
        server: str = Form("unknown"),
        tribe: str = Form("unknown"),
        post_visible: str = Form("0"),
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
        post_visible: str = Form("0"),
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
        x_gl_key: Optional[str] = Header(default=None, alias="X-GL-Key"),
        x_api_key: Optional[str] = Header(default=None, alias="x-api-key"),
    ) -> Dict[str, Any]:
        _require_key(settings, x_gl_key, x_api_key)
        if not settings.database_url:
            raise HTTPException(status_code=500, detail="DATABASE_URL not set")

        # allow the app to send a single line (already extracted)
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

        inserted = await app.state.db.insert_events(events)

        posted = 0
        if settings.log_posting_enabled and app.state.webhook is not None:
            for ev in inserted:
                do_ping = (
                    settings.critical_ping_enabled
                    and ev.severity == "CRITICAL"
                    and (settings.ping_all_critical or ev.category in settings.ping_categories)
                )
                await app.state.webhook.post_event_from_parsed(
                    ev,
                    mention_role_id=settings.critical_ping_role_id,
                    mention=do_ping,
                    env=settings.environment,
                )
                posted += 1
                if settings.post_delay_seconds > 0:
                    await asyncio.sleep(settings.post_delay_seconds)

        return {"ok": True, "inserted_events": len(inserted), "posted_events": posted}

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
