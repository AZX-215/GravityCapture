import os
import re
import asyncio
from typing import Optional, List, Set, Any

import asyncpg
import httpx
from fastapi import FastAPI, Header, HTTPException, UploadFile, File, Query, Request
from pydantic import BaseModel, Field

# OCR router expects raw image bytes and returns a dict
from ocr.router import extract_text

APP_ENV = os.getenv("ENVIRONMENT", "stage")
DATABASE_URL = os.getenv("DATABASE_URL", "")
SHARED = os.getenv("GL_SHARED_SECRET", "")

# ---- alerts config ----
LOG_POSTING_ENABLED = os.getenv("LOG_POSTING_ENABLED", "false").lower() == "true"
WEBHOOK_URL = os.getenv("ALERT_DISCORD_WEBHOOK_URL", "")

def _csv(name: str, default: str = "") -> List[str]:
    raw = os.getenv(name, default)
    if not raw:
        return []
    return [s.strip().upper() for s in raw.split(",") if s.strip()]

ALERT_SEVERITIES: Set[str] = set(_csv("ALERT_SEVERITIES", "CRITICAL,IMPORTANT"))
ALERT_CATEGORIES: Set[str] = set(_csv("ALERT_CATEGORIES", ""))  # empty -> all

app = FastAPI()
_pool: Optional[asyncpg.Pool] = None
_http: Optional[httpx.AsyncClient] = None


class TribeEvent(BaseModel):
    server: str
    tribe: str
    ark_day: int = Field(0, ge=0)
    ark_time: str = ""
    severity: str = "INFO"
    category: str = "GENERAL"
    actor: str = "Unknown"
    message: str
    raw_line: str


# ---------- startup/shutdown ----------
@app.on_event("startup")
async def _start():
    global _pool, _http
    _pool = await asyncpg.create_pool(DATABASE_URL, min_size=1, max_size=3)
    async with _pool.acquire() as con:
        await con.execute(
            """
            create table if not exists tribe_events (
              id serial primary key,
              ingested_at timestamptz not null default now(),
              server   text not null,
              tribe    text not null,
              ark_day  integer not null,
              ark_time text not null,
              severity text not null,
              category text not null,
              actor    text not null,
              message  text not null,
              raw_line text not null
            );
            """
        )
        await con.execute(
            "create unique index if not exists tribe_events_raw_line_uidx on tribe_events(raw_line);"
        )
        await con.execute(
            "create index if not exists tribe_events_id_desc_idx on tribe_events(id desc);"
        )

    _http = httpx.AsyncClient(timeout=10)

@app.on_event("startup")
async def warm_ocr():
    """Warm OCR using an in-memory PNG. Do not fail startup on error."""
    try:
        from PIL import Image
        import io
        img = Image.new("RGB", (256, 64), color=(255, 255, 255))
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        png = buf.getvalue()
        # Tesseract-only warmup to avoid cv2/libGL requirements
        extract_text(png, engine_hint="tess")
        print("[ocr] warmup ok")
    except Exception as e:
        print(f"[ocr] warmup error: {e}")

@app.on_event("shutdown")
async def _stop():
    global _pool, _http
    if _http:
        await _http.aclose()
    if _pool:
        await _pool.close()


# ---------- helpers ----------
def _authorized(key: Optional[str]) -> bool:
    return bool(SHARED) and (key == SHARED)

def _should_alert(evt: TribeEvent) -> bool:
    if not LOG_POSTING_ENABLED or not WEBHOOK_URL:
        return False
    sev_ok = (not ALERT_SEVERITIES) or (evt.severity.upper() in ALERT_SEVERITIES)
    cat_ok = (not ALERT_CATEGORIES) or (evt.category.upper() in ALERT_CATEGORIES)
    return sev_ok and cat_ok

async def _post_discord(evt: TribeEvent):
    if not _http or not WEBHOOK_URL:
        return
    color = 0xE74C3C if evt.severity.upper() == "CRITICAL" else 0xF1C40F
    embed = {
        "title": f"{evt.category} • {evt.severity}",
        "color": color,
        "fields": [
            {"name": "Server", "value": evt.server, "inline": True},
            {"name": "Tribe", "value": evt.tribe, "inline": True},
            {"name": "ARK Time", "value": f"Day {evt.ark_day}, {evt.ark_time}", "inline": True},
            {"name": "Actor", "value": evt.actor or "—", "inline": False},
            {"name": "Msg", "value": evt.message[:1000], "inline": False},
        ],
        "footer": {"text": f"env={APP_ENV}"},
    }
    payload = {"embeds": [embed], "content": None}
    try:
        r = await _http.post(WEBHOOK_URL, json=payload)
        if r.status_code not in (200, 204):
            print(f"[alert] webhook failed: {r.status_code} {r.text[:200]}")
    except Exception as e:
        print(f"[alert] exception: {type(e).__name__}: {e}")


# ---- dedupe helpers ----
STARVE_RX = re.compile(r"\bstarved\s+to\s+death\b", re.I)
KILLED_RX = re.compile(r"\bwas\s+killed\b", re.I)
TAME_ID_RX = re.compile(
    r"(?:Your\s+)?(?P<tame>.+?)\s+(?:starved\s+to\s+death|was\s+killed|was\s+ground\s+up|\bwas\s+destroyed\b)",
    re.I,
)

def _tame_identity(msg: str) -> str:
    m = TAME_ID_RX.search(msg or "")
    ident = (m.group("tame") if m else "").strip()
    return re.sub(r"\s+", " ", ident).strip().lower()

def _is_starved(msg: str) -> bool:
    return bool(STARVE_RX.search(msg or ""))

def _is_killed(msg: str) -> bool:
    return bool(KILLED_RX.search(msg or ""))


# ---------- routes ----------
@app.get("/health")
async def health():
    try:
        async with _pool.acquire() as con:  # type: ignore
            ver = await con.fetchval("select version()")
        return {"ok": True, "env": APP_ENV, "db": bool(ver)}
    except Exception as e:
        return {"ok": False, "env": APP_ENV, "error": f"{type(e).__name__}: {e}"}

@app.post("/api/tribe-events")
async def ingest(evt: TribeEvent, x_gl_key: Optional[str] = Header(None)):
    if not _authorized(x_gl_key):
        raise HTTPException(status_code=401, detail="unauthorized")

    fam_ident = _tame_identity(evt.message or evt.raw_line)
    deduped = False
    try:
        async with _pool.acquire() as con:  # type: ignore
            rows = await con.fetch(
                """
                select id, message
                  from tribe_events
                 where server=$1 and tribe=$2 and ark_day=$3 and ark_time=$4
                   and ingested_at >= (now() - interval '2 minutes')
                 order by id desc
                """,
                evt.server, evt.tribe, evt.ark_day, evt.ark_time
            )

            for r in rows:
                if _tame_identity(r["message"]) == fam_ident:
                    if _is_starved(r["message"]) and _is_killed(evt.message):
                        deduped = True
                    elif _is_killed(r["message"]) and _is_starved(evt.message):
                        await con.execute("delete from tribe_events where id=$1", r["id"])
                    break

            if deduped:
                return {"ok": True, "deduped": True, "alerted": False, "env": APP_ENV}

            status: str = await con.execute(
                """
                insert into tribe_events
                  (server, tribe, ark_day, ark_time, severity, category, actor, message, raw_line)
                values ($1,$2,$3,$4,$5,$6,$7,$8,$9)
                on conflict (raw_line) do nothing
                """,
                evt.server, evt.tribe, evt.ark_day, evt.ark_time,
                evt.severity, evt.category, evt.actor, evt.message, evt.raw_line
            )
            deduped = status.strip().endswith("0")
    except Exception as e:
        print(f"[db] insert error: {type(e).__name__}: {e}")
        raise HTTPException(status_code=500, detail="db insert failed")

    alerted = False
    if not deduped and _should_alert(evt):
        asyncio.create_task(_post_discord(evt))
        alerted = True

    return {"ok": True, "deduped": deduped, "alerted": alerted, "env": APP_ENV}

@app.get("/api/tribe-events/recent")
async def recent(server: Optional[str] = None, tribe: Optional[str] = None, limit: int = 20):
    limit = max(1, min(int(limit), 100))
    try:
        async with _pool.acquire() as con:  # type: ignore
            where = []
            args: List[Any] = []
            if server:
                where.append(f"server = ${len(args)+1}")
                args.append(server)
            if tribe:
                where.append(f"tribe = ${len(args)+1}")
                args.append(tribe)

            sql = """
                select id, ingested_at, server, tribe, ark_day, ark_time,
                       severity, category, actor, message
                from tribe_events
            """
            if where:
                sql += " where " + " and ".join(where)
            sql += f" order by id desc limit ${len(args)+1}"
            args.append(limit)

            rows = await con.fetch(sql, *args)
            return [dict(r) for r in rows]
    except Exception as e:
        print(f"[db] recent error: {type(e).__name__}: {e}")
        raise HTTPException(status_code=500, detail="db query failed")

# ---------- OCR route ----------
@app.post("/api/ocr/extract")
async def ocr_extract(
    request: Request,
    file: UploadFile | None = File(None),
    image: UploadFile | None = File(None),
    engine: str = Query("auto"),
):
    """Extract text from an image.

    Accepts **either** a multipart form field named `file` **or** `image`.
    Also supports posting **raw image bytes** with a `Content-Type: image/*` header.
    """
    up = file or image
    data: bytes
    content_type: str

    if up is not None:
        content_type = (up.content_type or "").lower()
        if not content_type.startswith("image/"):
            raise HTTPException(status_code=400, detail="Uploaded part must be an image (png, jpeg, webp).")
        data = await up.read()
    else:
        # No multipart field provided: try raw body
        content_type = (request.headers.get("content-type") or "").lower()
        if not content_type.startswith("image/"):
            raise HTTPException(
                status_code=422,
                detail="No file provided. Send multipart form-data with field 'file' or 'image', "
                       "or send raw image bytes with Content-Type: image/*."
            )
        data = await request.body()

    # Call OCR (returns a dict). Keep response shape compatible: engine/conf/lines.
    res = extract_text(data, engine_hint=engine)
    if not isinstance(res, dict):
        # Fallback if an older router returns a tuple
        try:
            text, conf, lines = res  # type: ignore[misc]
            return {"engine": engine, "conf": conf, "lines": lines}
        except Exception:
            raise HTTPException(status_code=500, detail="Unexpected OCR return type")

    return {
        "engine": res.get("engine", engine),
        "conf": res.get("conf"),
        "lines": res.get("lines"),
    }
