from __future__ import annotations

import asyncio
import logging
import os
from typing import Any, Dict, List, Optional

import httpx

logger = logging.getLogger("gravitycapture")


def _truthy(v: Optional[str]) -> bool:
    return (v or "").strip().lower() in ("1", "true", "yes", "y", "on", "enable", "enabled")


def _get_env(name: str, alt: Optional[str] = None) -> str:
    v = (os.getenv(name) or (os.getenv(alt) if alt else "") or "").strip()
    return v


def _discord_base_url(app_id: str, guild_id: str) -> str:
    if guild_id:
        return f"https://discord.com/api/v10/applications/{app_id}/guilds/{guild_id}/commands"
    return f"https://discord.com/api/v10/applications/{app_id}/commands"


def _desired_commands() -> List[Dict[str, Any]]:
    # Chat input command (slash command)
    return [
        {
            "name": "gravitycapture",
            "description": "Show Gravity Capture dashboard + download link",
            "type": 1,
            "dm_permission": True,
        },
        {
            "name": "download_gravity_capture",
            "description": "Get the latest Gravity Capture download link",
            "type": 1,
            "dm_permission": True,
        },
    ]


async def _request_with_retry(
    client: httpx.AsyncClient,
    method: str,
    url: str,
    *,
    json_body: Any = None,
    headers: Dict[str, str],
    max_attempts: int = 4,
) -> httpx.Response:
    last_exc: Optional[Exception] = None
    for attempt in range(1, max_attempts + 1):
        try:
            r = await client.request(method, url, json=json_body, headers=headers)
            # Rate-limited: retry after suggested delay (Discord uses seconds)
            if r.status_code == 429:
                try:
                    data = r.json()
                    retry_after = float(data.get("retry_after") or 1.0)
                except Exception:
                    retry_after = 1.0
                await asyncio.sleep(min(max(retry_after, 0.5), 10.0))
                continue
            return r
        except Exception as e:
            last_exc = e
            await asyncio.sleep(min(0.5 * attempt, 2.0))
    raise last_exc or RuntimeError("Discord request failed with unknown error")


async def register_commands_if_enabled() -> None:
    """
    Auto-register slash commands on startup (optional).

    Enable by setting:
      DISCORD_AUTO_REGISTER=1
    Required:
      DISCORD_BOT_TOKEN, DISCORD_APPLICATION_ID
    Optional:
      DISCORD_GUILD_ID  (if set, registers as guild commands for immediate availability)
    """
    if not _truthy(os.getenv("DISCORD_AUTO_REGISTER", "0")):
        return

    token = _get_env("DISCORD_BOT_TOKEN", "DISCORD_TOKEN")
    app_id = _get_env("DISCORD_APPLICATION_ID", "DISCORD_APP_ID")
    guild_id = _get_env("DISCORD_GUILD_ID")

    if not token or not app_id:
        logger.warning("Discord auto-register enabled but DISCORD_BOT_TOKEN or DISCORD_APPLICATION_ID is missing; skipping.")
        return

    base_url = _discord_base_url(app_id, guild_id)

    headers = {
        "Authorization": f"Bot {token}",
        "Content-Type": "application/json",
        # Conservative UA helps with diagnostics if Discord ever asks.
        "User-Agent": "GravityCapture (https://github.com/AZX-215/GravityCapture, 1.0)",
    }

    desired = _desired_commands()

    timeout = httpx.Timeout(15.0, connect=10.0)
    async with httpx.AsyncClient(timeout=timeout) as client:
        # List existing commands (do not delete unknown commands).
        r = await _request_with_retry(client, "GET", base_url, headers=headers)
        if r.status_code >= 400:
            logger.warning("Discord auto-register: GET commands failed (%s): %s", r.status_code, r.text[:300])
            return

        try:
            existing = r.json() or []
        except Exception:
            existing = []

        by_name = {str(c.get("name") or "").lower(): c for c in existing if isinstance(c, dict)}

        created = 0
        updated = 0

        for cmd in desired:
            name = str(cmd.get("name") or "").lower()
            cur = by_name.get(name)
            if cur and cur.get("id"):
                # Update existing
                cmd_id = cur["id"]
                url = f"{base_url}/{cmd_id}"
                pr = await _request_with_retry(client, "PATCH", url, json_body=cmd, headers=headers)
                if pr.status_code < 300:
                    updated += 1
                else:
                    logger.warning("Discord auto-register: PATCH %s failed (%s): %s", name, pr.status_code, pr.text[:300])
            else:
                # Create new
                pr = await _request_with_retry(client, "POST", base_url, json_body=cmd, headers=headers)
                if pr.status_code < 300:
                    created += 1
                else:
                    logger.warning("Discord auto-register: POST %s failed (%s): %s", name, pr.status_code, pr.text[:300])

        scope = f"guild:{guild_id}" if guild_id else "global"
        logger.info("Discord auto-register complete (scope=%s): created=%s updated=%s", scope, created, updated)
