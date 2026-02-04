from __future__ import annotations

import json
import logging
import os
from typing import Any, Dict, Optional

from fastapi import APIRouter, Header, HTTPException, Request

logger = logging.getLogger("gravitycapture")

# We verify Discord Interaction signatures (Ed25519).
# Keep this import optional so the API will not crash if the dependency is missing.
try:
    from nacl.signing import VerifyKey  # type: ignore
    from nacl.exceptions import BadSignatureError  # type: ignore

    _HAS_NACL = True
except Exception:
    VerifyKey = None  # type: ignore
    BadSignatureError = Exception  # type: ignore
    _HAS_NACL = False


def _truthy(v: str) -> bool:
    return (v or "").strip().lower() in ("1", "true", "yes", "y", "on")


def _get_public_key() -> str:
    return (os.getenv("DISCORD_PUBLIC_KEY") or "").strip()


def _get_download_url() -> str:
    # Defaults to GitHub releases latest (safe, public).
    return (os.getenv("GRAVITYCAPTURE_DOWNLOAD_URL") or "https://github.com/AZX-215/GravityCapture/releases/latest").strip()


def _get_dashboard_url() -> str:
    return (os.getenv("GRAVITYCAPTURE_DASHBOARD_URL") or "").strip()


def _response_content() -> str:
    dl = _get_download_url()
    dash = _get_dashboard_url()
    if dash:
        return f"Gravity Capture dashboard: {dash}\nLatest download: {dl}"
    return f"Latest Gravity Capture download: {dl}"


def _verify_discord_signature(public_key_hex: str, signature_hex: str, timestamp: str, body: bytes) -> None:
    if not public_key_hex:
        raise HTTPException(status_code=503, detail="DISCORD_PUBLIC_KEY not set")
    if not _HAS_NACL:
        raise HTTPException(status_code=503, detail="PyNaCl not installed (signature verification unavailable)")

    try:
        verify_key = VerifyKey(bytes.fromhex(public_key_hex))  # type: ignore[arg-type]
        message = timestamp.encode("utf-8") + body
        verify_key.verify(message, bytes.fromhex(signature_hex))  # type: ignore[call-arg]
    except BadSignatureError:
        raise HTTPException(status_code=401, detail="Bad request signature")
    except Exception as e:
        logger.exception("Discord signature verification error: %s", e)
        raise HTTPException(status_code=400, detail="Invalid signature headers")


router = APIRouter()


@router.post("/discord/interactions")
async def discord_interactions(
    request: Request,
    x_signature_ed25519: Optional[str] = Header(default=None, alias="X-Signature-Ed25519"),
    x_signature_timestamp: Optional[str] = Header(default=None, alias="X-Signature-Timestamp"),
) -> Dict[str, Any]:
    body = await request.body()

    # Verify signature
    _verify_discord_signature(
        public_key_hex=_get_public_key(),
        signature_hex=(x_signature_ed25519 or ""),
        timestamp=(x_signature_timestamp or ""),
        body=body,
    )

    try:
        payload = json.loads(body.decode("utf-8"))
    except Exception:
        raise HTTPException(status_code=400, detail="Invalid JSON")

    t = payload.get("type")

    # 1 = PING
    if t == 1:
        return {"type": 1}

    # 2 = APPLICATION_COMMAND
    if t == 2:
        data = payload.get("data") or {}
        name = (data.get("name") or "").lower().strip()

        # Support a few common command names
        if name in {"gravitycapture", "download", "gc"}:
            flags = 64 if _truthy(os.getenv("DISCORD_EPHEMERAL", "0")) else 0
            resp: Dict[str, Any] = {
                "type": 4,
                "data": {
                    "content": _response_content(),
                },
            }
            if flags:
                resp["data"]["flags"] = flags
            return resp

        # Unknown command -> ephemeral help
        return {
            "type": 4,
            "data": {
                "content": "Unknown command. Try /gravitycapture",
                "flags": 64,
            },
        }

    # 3 = MESSAGE_COMPONENT, etc. Not used.
    return {"type": 4, "data": {"content": "Unsupported interaction type.", "flags": 64}}
