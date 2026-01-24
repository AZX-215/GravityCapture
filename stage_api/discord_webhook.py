from __future__ import annotations

from typing import Any, Dict, List, Optional

import httpx

from tribelog.models import ParsedEvent


def _severity_color(severity: str) -> int:
    sev = (severity or "").upper()
    if sev == "CRITICAL":
        return 0xE53935  # red
    if sev in ("WARNING", "WARN"):
        return 0xFDD835  # yellow
    if sev in ("SUCCESS", "OK", "GOOD"):
        return 0x43A047  # green
    return 0x78909C  # blue-grey


def _field(name: str, value: str, inline: bool = True) -> Dict[str, Any]:
    v = (value or "").strip()
    if not v:
        v = "-"
    if len(v) > 1024:
        v = v[:1021] + "..."
    return {"name": name, "value": v, "inline": inline}


class DiscordWebhookClient:
    def __init__(self, webhook_url: str, *, post_delay_seconds: float = 0.8) -> None:
        self._webhook_url = (webhook_url or "").strip()
        self._client = httpx.AsyncClient(timeout=20)
        self._post_delay_seconds = float(post_delay_seconds or 0.0)

    async def aclose(self) -> None:
        await self._client.aclose()

    async def post_event_from_parsed(
        self,
        ev: ParsedEvent,
        *,
        mention_role_id: str,
        mention: bool,
        env: str,
    ) -> None:
        if not self._webhook_url:
            return

        content = ""
        allowed_roles: Optional[List[str]] = None
        if mention and mention_role_id:
            content = f"<@&{mention_role_id}>"
            allowed_roles = [str(mention_role_id)]

        title = f"{ev.category.replace('_', ' ')}"
        footer = f"{env} • Day {ev.ark_day}, {ev.ark_time}"

        fields = [
            _field("Server", ev.server, True),
            _field("Tribe", ev.tribe, True),
            _field("Severity", ev.severity, True),
            _field("Actor", ev.actor or "-", True),
            _field("Message", ev.message, False),
        ]

        payload: Dict[str, Any] = {
            "content": content,
            "embeds": [
                {
                    "title": title,
                    "color": _severity_color(ev.severity),
                    "fields": fields,
                    "footer": {"text": footer},
                }
            ],
            "allowed_mentions": {"parse": [], "roles": allowed_roles or []},
        }

        resp = await self._client.post(self._webhook_url, json=payload)
        resp.raise_for_status()
