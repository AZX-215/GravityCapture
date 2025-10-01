# api_main.py
# FastAPI service for Gravity Capture (stage API)
# - Stable OCR endpoint aliases
# - Joined `text` field in the response
# - Compatible /ingest endpoints used by the desktop app

from __future__ import annotations

import io
import os
import sys
import traceback
from typing import Any, Dict, List, Optional, Tuple

from fastapi import (
    FastAPI,
    File,
    UploadFile,
    HTTPException,
    Request,
    Body,
    Form,
)
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel

# ---------- optional dependencies ----------
# We try EasyOCR first, then pytesseract, else return 501
EASYOCR_READER = None
EASYOCR_ERR: Optional[str] = None
PYTESSERACT_AVAILABLE = False
PYTESSERACT_ERR: Optional[str] = None

try:
    import easyocr  # type: ignore
    # suppress stdout from easyocr initialization
    try:
        EASYOCR_READER = easyocr.Reader(["en"], gpu=False, verbose=False)
    except Exception as _e:
        EASYOCR_ERR = f"easyocr init failed: {_e}"
except Exception as _e:
    EASYOCR_ERR = f"easyocr import failed: {_e}"

try:
    import pytesseract  # type: ignore
    from pytesseract import Output  # type: ignore

    PYTESSERACT_AVAILABLE = True
except Exception as _e:
    PYTESSERACT_ERR = f"pytesseract import failed: {_e}"

try:
    from PIL import Image  # type: ignore
except Exception:
    raise RuntimeError("Pillow is required")

try:
    import numpy as np  # type: ignore
except Exception:
    np = None  # only needed for easyocr


# ---------- environment ----------
APP_ENV = os.getenv("ENVIRONMENT", "stage")
LOG_POSTING_ENABLED = os.getenv("LOG_POSTING_ENABLED", "false").lower() == "true"
DISCORD_WEBHOOK = os.getenv("ALERT_DISCORD_WEBHOOK_URL", "").strip()
GL_SHARED_SECRET = os.getenv("GL_SHARED_SECRET", "").strip()

# httpx only used if webhook present
HTTPX_AVAILABLE = False
try:
    import httpx  # type: ignore

    HTTPX_AVAILABLE = True
except Exception:
    pass


# ---------- models ----------
class LogLineIngest(BaseModel):
    line: str
    server: Optional[str] = ""
    tribe: Optional[str] = ""


# ---------- app ----------
app = FastAPI(title="Gravity Capture Stage API", version="1.2.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=False,
    allow_methods=["*"],
    allow_headers=["*"],
)


# ---------- utils ----------
def _avg(values: List[float]) -> Optional[float]:
    vals = [v for v in values if v is not None]
    if not vals:
        return None
    return float(sum(vals) / len(vals))


def _pil_from_bytes(b: bytes) -> Image.Image:
    try:
        return Image.open(io.BytesIO(b)).convert("RGB")
    except Exception:
        raise HTTPException(status_code=400, detail="Invalid image bytes")


def _easyocr_extract(img: Image.Image) -> Tuple[str, Optional[float], List[Dict[str, Any]]]:
    if EASYOCR_READER is None:
        raise HTTPException(status_code=501, detail=EASYOCR_ERR or "easyocr not available")
    if np is None:
        raise HTTPException(status_code=501, detail="numpy not available for easyocr")
    arr = np.array(img)
    # result: list of [bbox, text, conf]
    result = EASYOCR_READER.readtext(arr, detail=1, paragraph=False)
    lines: List[Dict[str, Any]] = []
    confs: List[float] = []
    texts: List[str] = []
    for item in result:
        try:
            bbox, text, conf = item
        except Exception:
            # older formats
            bbox, text, conf = item[0], item[1], item[2] if len(item) > 2 else None
        texts.append(text or "")
        if conf is not None:
            try:
                confs.append(float(conf))
            except Exception:
                pass
        lines.append({"text": text or "", "conf": None if conf is None else float(conf), "bbox": bbox})
    joined = "\n".join([t for t in texts if t]).strip()
    return joined, _avg(confs), lines


def _tesseract_extract(img: Image.Image) -> Tuple[str, Optional[float], List[Dict[str, Any]]]:
    if not PYTESSERACT_AVAILABLE:
        raise HTTPException(status_code=501, detail=PYTESSERACT_ERR or "pytesseract not available")
    data = pytesseract.image_to_data(img, output_type=Output.DICT)  # type: ignore
    n = len(data.get("text", []))
    lines: List[Dict[str, Any]] = []
    confs: List[float] = []
    pieces: List[str] = []
    for i in range(n):
        txt = (data["text"][i] or "").strip()
        if not txt:
            continue
        try:
            conf = float(data["conf"][i])
            # tesseract returns -1 for missing
            conf = None if conf < 0 else conf
        except Exception:
            conf = None
        if conf is not None:
            confs.append(conf)
        bbox = {
            "x": int(data.get("left", [0])[i]),
            "y": int(data.get("top", [0])[i]),
            "w": int(data.get("width", [0])[i]),
            "h": int(data.get("height", [0])[i]),
        }
        pieces.append(txt)
        lines.append({"text": txt, "conf": conf, "bbox": bbox})
    joined = " ".join(pieces).strip()
    return joined, _avg(confs), lines


def extract_text(image_bytes: bytes, engine_hint: str = "auto") -> Dict[str, Any]:
    img = _pil_from_bytes(image_bytes)

    engine_used = None
    text: str = ""
    conf: Optional[float] = None
    lines: List[Dict[str, Any]] = []

    # choose engine
    if engine_hint in ("auto", "easyocr"):
        try:
            text, conf, lines = _easyocr_extract(img)
            engine_used = "easyocr"
        except HTTPException:
            if engine_hint == "easyocr":
                raise
        except Exception as e:
            # fall back
            pass

    if engine_used is None and engine_hint in ("auto", "tesseract"):
        try:
            text, conf, lines = _tesseract_extract(img)
            engine_used = "tesseract"
        except HTTPException:
            if engine_hint == "tesseract":
                raise
        except Exception:
            pass

    if engine_used is None:
        # neither available
        raise HTTPException(
            status_code=501,
            detail=(
                "No OCR engine available. "
                f"{EASYOCR_ERR or ''} | {PYTESSERACT_ERR or ''}".strip()
            ),
        )

    return {
        "engine": engine_used,
        "conf": conf,
        "lines": lines,
        "text": text,
    }


async def _read_image_from_request(
    request: Request, file: UploadFile | None, image: UploadFile | None
) -> bytes:
    up = file or image
    if up is not None:
        ct = (up.content_type or "").lower()
        if not ct.startswith("image/"):
            raise HTTPException(status_code=400, detail="Uploaded part must be an image (png, jpeg, webp).")
        return await up.read()

    # allow raw image bytes
    ct = (request.headers.get("content-type") or "").lower()
    if not ct.startswith("image/"):
        raise HTTPException(
            status_code=422,
            detail="No file provided. Send multipart field 'file' or 'image', or raw image bytes with Content-Type: image/*.",
        )
    return await request.body()


# ---------- routes ----------
@app.get("/")
async def root():
    return {"service": "gravity-capture-stage-api", "env": APP_ENV, "ok": True}


@app.get("/healthz")
async def healthz():
    return {"ok": True, "env": APP_ENV}


# All OCR aliases used by the desktop app or experiments
@app.post("/extract")
@app.post("/api/extract")
@app.post("/ocr")
@app.post("/ocr/extract")
@app.post("/api/ocr")
@app.post("/api/ocr/extract")
async def ocr_extract(
    request: Request,
    file: UploadFile | None = File(None),
    image: UploadFile | None = File(None),
    engine: str = "auto",
):
    try:
        data = await _read_image_from_request(request, file, image)
        res = extract_text(data, engine_hint=engine)
        return res
    except HTTPException:
        raise
    except Exception as e:
        traceback.print_exc()
        raise HTTPException(status_code=500, detail=str(e))


# temporary but compatible ingest handlers
@app.post("/ingest/screenshot")
@app.post("/api/ingest/screenshot")
async def ingest_screenshot(
    file: UploadFile = File(...),
    server: str = Form(""),
    tribe: str = Form(""),
    post_visible: str = Form("0"),
):
    img_bytes = await file.read()
    ok = True
    posted = False
    err: Optional[str] = None

    if LOG_POSTING_ENABLED and DISCORD_WEBHOOK and HTTPX_AVAILABLE:
        try:
            content = f"[{APP_ENV}] Screenshot from server='{server}' tribe='{tribe}' visible={post_visible}"
            files = {"file": (file.filename or "visible.jpg", img_bytes, file.content_type or "image/jpeg")}
            async with httpx.AsyncClient(timeout=15) as client:
                r = await client.post(DISCORD_WEBHOOK, data={"content": content}, files=files)
                posted = r.status_code < 300
        except Exception as e:
            err = f"webhook error: {e}"

    return {"ok": ok, "posted": posted, "env": APP_ENV, "error": err}


@app.post("/ingest/log-line")
@app.post("/api/ingest/log-line")
async def ingest_log_line(payload: LogLineIngest = Body(...)):
    posted = False
    err: Optional[str] = None

    if LOG_POSTING_ENABLED and DISCORD_WEBHOOK and HTTPX_AVAILABLE:
        try:
            content = f"[{APP_ENV}] {payload.server or ''} / {payload.tribe or ''}\n```\n{payload.line}\n```"
            async with httpx.AsyncClient(timeout=15) as client:
                r = await client.post(DISCORD_WEBHOOK, json={"content": content})
                posted = r.status_code < 300
        except Exception as e:
            err = f"webhook error: {e}"

    return {"ok": True, "posted": posted, "env": APP_ENV, "error": err, "echo": payload.dict()}


# ---------- uvicorn entry ----------
if __name__ == "__main__":
    import uvicorn

    port = int(os.getenv("PORT", "8080"))
    uvicorn.run("api_main:app", host="0.0.0.0", port=port, reload=False)
