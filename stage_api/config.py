from __future__ import annotations

import os
from dataclasses import dataclass
from typing import Set


def _get_bool(name: str, default: bool = False) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() in {"1", "true", "yes", "y", "on"}


def _get_float(name: str, default: float) -> float:
    raw = os.getenv(name)
    if raw is None:
        return default
    try:
        return float(raw)
    except ValueError:
        return default


def _get_csv(name: str, default_csv: str = "") -> Set[str]:
    raw = os.getenv(name, default_csv)
    parts = [p.strip() for p in raw.split(",") if p.strip()]
    return set(parts)


@dataclass(frozen=True)
class Settings:
    # Auth (optional legacy shared secret)
    gl_shared_secret: str

    # Multi-tenant mode
    tenants_enabled: bool
    tenants_bootstrap_legacy: bool
    legacy_tenant_name: str

    # Database
    database_url: str

    # Discord (legacy defaults)
    alert_discord_webhook_url: str
    log_posting_enabled: bool
    post_delay_seconds: float

    # If true, Discord posting runs in the background and the API responds immediately.
    async_posting_enabled: bool

    # Pings (legacy defaults)
    critical_ping_enabled: bool
    critical_ping_role_id: str

    # Option B: restrict pings to selected CRITICAL categories
    ping_all_critical: bool
    ping_categories: Set[str]

    # OCR
    ocr_engine: str  # auto | ppocr | tesseract

    # General
    environment: str

    @staticmethod
    def from_env() -> "Settings":
        # Shared secret can be any string; the desktop app sends it in X-GL-Key / x-api-key.
        gl_shared_secret = (os.getenv("GL_SHARED_SECRET") or os.getenv("SCREENSHOT_AGENT_KEY") or "").strip()

        return Settings(
            gl_shared_secret=gl_shared_secret,
            tenants_enabled=_get_bool("TENANTS_ENABLED", False),
            tenants_bootstrap_legacy=_get_bool("TENANTS_BOOTSTRAP_LEGACY", True),
            legacy_tenant_name=(os.getenv("LEGACY_TENANT_NAME") or "legacy").strip() or "legacy",
            database_url=(os.getenv("DATABASE_URL") or "").strip(),
            alert_discord_webhook_url=(os.getenv("ALERT_DISCORD_WEBHOOK_URL") or "").strip(),
            log_posting_enabled=_get_bool("LOG_POSTING_ENABLED", True),
            post_delay_seconds=_get_float("POST_DELAY_SECONDS", 0.8),
            async_posting_enabled=_get_bool("ASYNC_POSTING_ENABLED", True),
            critical_ping_enabled=_get_bool("CRITICAL_PING_ENABLED", True),
            critical_ping_role_id=(os.getenv("CRITICAL_PING_ROLE_ID") or "1286835166471262249").strip(),
            ping_all_critical=_get_bool("PING_ALL_CRITICAL", False),
            ping_categories=_get_csv("PING_CATEGORIES", "STRUCTURE_DESTROYED,TRIBE_KILLED_PLAYER"),
            ocr_engine=(os.getenv("OCR_ENGINE") or "auto").strip().lower(),
            environment=(os.getenv("ENVIRONMENT") or os.getenv("ENV") or "stage").strip() or "stage",
        )
