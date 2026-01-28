from __future__ import annotations

import os
import re
from io import BytesIO
from typing import Any, Dict, List, Optional, Tuple

import numpy as np
from PIL import Image, ImageEnhance, ImageFilter, ImageOps, ImageFile

import cv2 as cv

from .engines import make_extractor  # type: ignore
from .schema import Line
from .repair.normalize import normalize, mean_conf

ImageFile.LOAD_TRUNCATED_IMAGES = True


# "Header hit-rate" matcher for ARK tribe logs.
# Intentionally tolerant: OCR can confuse Day/Dav/Doy and punctuation.
_RX_DAY_HEADER = re.compile(
    r"^\s*(?:Day|Dav|Doy)\s*[,/:\-]?\s*\d{2,6}\s*[, ]\s*\d{1,2}\s*[:.]\s*\d{1,2}(?:\s*[:.]\s*\d{2,3})?",
    re.IGNORECASE,
)


def _load_pil(image_bytes: bytes) -> Image.Image:
    return Image.open(BytesIO(image_bytes)).convert("RGB")


def _cap_width(pil_rgb: Image.Image, max_w: int = 1920) -> Image.Image:
    if pil_rgb.width <= max_w:
        return pil_rgb
    h = int(pil_rgb.height * (max_w / pil_rgb.width))
    return pil_rgb.resize((max_w, h), Image.LANCZOS)


def _pil_to_np_rgb(pil_rgb: Image.Image) -> np.ndarray:
    # PIL RGB -> np RGB uint8
    return np.asarray(pil_rgb, dtype=np.uint8)


def _otsu(gray: np.ndarray) -> np.ndarray:
    _, bw = cv.threshold(gray, 0, 255, cv.THRESH_BINARY + cv.THRESH_OTSU)
    return bw


def _ensure_white_bg(binary: np.ndarray) -> np.ndarray:
    # Prefer black text on white bg for Tesseract.
    return (255 - binary) if binary.mean() < 127 else binary


def _variant_images(pil_rgb: Image.Image, *, max_w: int = 1920) -> List[Tuple[str, np.ndarray]]:
    """
    Returns list of (variant_name, gray_uint8) images.
    NOTE: Some variants are binary; we still return uint8 grayscale (0/255).
    """
    im = _cap_width(pil_rgb, max_w=max_w)
    np_rgb = _pil_to_np_rgb(im)
    np_bgr = cv.cvtColor(np_rgb, cv.COLOR_RGB2BGR)

    # Base grayscale + mild contrast/sharpen
    g_pil = ImageOps.grayscale(im)
    g_pil = ImageEnhance.Contrast(g_pil).enhance(1.8)
    g_pil = g_pil.filter(ImageFilter.UnsharpMask(radius=1.2, percent=140, threshold=3))
    g = np.asarray(g_pil, dtype=np.uint8)

    # Variant: enhanced grayscale
    enhanced = g

    # Variant: binary (Otsu)
    bw = _ensure_white_bg(_otsu(enhanced))

    # Variant: inverted (sometimes HDR)
    inv = 255 - bw

    # Variant: ARK UI (teal/blue background suppression)
    # LAB + CLAHE on L, then adaptive threshold.
    lab = cv.cvtColor(np_bgr, cv.COLOR_BGR2LAB)
    l, a, b = cv.split(lab)
    clahe = cv.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    l2 = clahe.apply(l)
    lab2 = cv.merge((l2, a, b))
    bgr2 = cv.cvtColor(lab2, cv.COLOR_LAB2BGR)
    g2 = cv.cvtColor(bgr2, cv.COLOR_BGR2GRAY)

    # unsharp
    blur = cv.GaussianBlur(g2, (0, 0), 1.0)
    sharp = cv.addWeighted(g2, 1.6, blur, -0.6, 0)

    ark_bw = cv.adaptiveThreshold(
        sharp, 255, cv.ADAPTIVE_THRESH_GAUSSIAN_C, cv.THRESH_BINARY, 31, 10
    )
    ark_bw = _ensure_white_bg(ark_bw)

    return [
        ("ark_ui", ark_bw),
        ("enhanced", enhanced),
        ("binary", bw),
        ("inverted", inv),
    ]


def _run_engine(engine_name: str, gray_np: np.ndarray) -> List[Line]:
    ext = make_extractor(engine_name)
    lines = ext.run(gray_np)  # List[Line]
    return normalize(lines)


def _joined_text(lines: List[Line]) -> str:
    return "\n".join([ln.text.strip() for ln in lines if ln.text and ln.text.strip()])


def _header_hits(lines: List[Line]) -> int:
    hits = 0
    for ln in lines:
        s = (ln.text or "").strip()
        if not s:
            continue
        if _RX_DAY_HEADER.match(s):
            hits += 1
    return hits


def _env_bool(name: str, default: bool = False) -> bool:
    v = os.getenv(name)
    if v is None:
        return default
    v = str(v).strip().lower()
    return v in {"1", "true", "yes", "y", "on", "enable", "enabled"}


def extract_text(image_bytes: bytes, engine_hint: str = "auto", *, fast: bool = False) -> Dict[str, Any]:
    """
    High-level OCR entry point used by the API.
    Strategy:
      - Generate multiple preprocessing variants (including ARK UI suppression)
      - Run OCR engines (auto tries ppocr then tesseract)
      - Choose the best candidate by:
           1) header_hit_rate (count of lines matching ARK "Day ..." header)
           2) mean confidence as tie-breaker
    """
    pil = _load_pil(image_bytes)

    # Fast mode is designed to keep request latency low for the desktop client.
    # It limits the number of OCR runs while still being robust for ARK tribe logs.
    fast = bool(fast) or _env_bool("OCR_FAST_DEFAULT", default=False)

    hint = (engine_hint or "auto").strip().lower()
    if hint in ("ppocr", "rapidocr", "paddle"):
        engines = ["ppocr"]
    elif hint in ("tess", "tesseract"):
        engines = ["tesseract"]
    else:
        # For this project, Tesseract is the primary engine.
        # Auto mode used to try multiple engines, which can be too slow on Railway.
        engines = ["tesseract"]

    max_w = int(os.getenv("OCR_MAX_WIDTH_FAST" if fast else "OCR_MAX_WIDTH", "1400" if fast else "1920"))
    variants = _variant_images(pil, max_w=max_w)

    # Prefer ARK UI suppression first. In fast mode, try only a small fallback set.
    preferred = ["ark_ui", "enhanced", "binary", "inverted"]
    variants.sort(key=lambda t: preferred.index(t[0]) if t[0] in preferred else 999)
    if fast:
        try_max = int(os.getenv("OCR_MAX_VARIANTS_FAST", "2"))
        variants = variants[: max(1, min(len(variants), try_max))]

    best: Optional[Dict[str, Any]] = None
    best_key = (-1, -1.0)  # (header_hits, mean_conf)

    candidates: List[Dict[str, Any]] = []

    accept_hits = int(os.getenv("OCR_ACCEPT_HEADER_HITS", "1"))
    accept_conf = float(os.getenv("OCR_ACCEPT_CONF", "0.45"))

    for vname, gray in variants:
        for eng in engines:
            try:
                lines = _run_engine(eng, gray)
            except Exception:
                continue
            if not lines:
                continue

            hits = _header_hits(lines)
            mc = float(mean_conf(lines))

            candidates.append(
                {
                    "engine": eng,
                    "variant": vname,
                    "header_hits": hits,
                    "mean_conf": mc,
                    "line_count": len(lines),
                }
            )

            key = (hits, mc)
            if key > best_key:
                best_key = key
                best = {
                    "engine": eng,
                    "variant": vname,
                    "conf": mc,
                    "lines": [{"text": ln.text, "conf": float(ln.conf), "bbox": list(ln.bbox)} for ln in lines],
                    "lines_text": [ln.text for ln in lines if ln.text and ln.text.strip()],
                    "text": _joined_text(lines),
                }

            # Early accept to keep latency low.
            if fast and (hits >= accept_hits or mc >= accept_conf):
                break

        if fast and best is not None and (best_key[0] >= accept_hits or best_key[1] >= accept_conf):
            break

    if best is None:
        return {"engine": "none", "variant": "none", "conf": 0.0, "lines": [], "lines_text": [], "text": "", "candidates": []}

    # Include candidate stats for debugging (/extract UI and logs).
    best["candidates"] = sorted(candidates, key=lambda d: (d["header_hits"], d["mean_conf"]), reverse=True)

    return best
