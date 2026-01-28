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


# Critical-ish keywords. Used for variant selection/merge (red/magenta text tends to be critical).
_RX_CRITICAL = re.compile(
    r"(?:\bwas killed\b|\bkilled\b|\bdestroyed\b|\bdemolished\b|\bauto-?decay\b|\bremoved from the tribe\b)",
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


def _percentile_normalize(gray: np.ndarray, p_lo: float = 1.0, p_hi: float = 99.0) -> np.ndarray:
    """Contrast-normalize a uint8 grayscale image by percentile clipping."""
    g = gray.astype(np.float32, copy=False)
    lo = float(np.percentile(g, p_lo))
    hi = float(np.percentile(g, p_hi))
    if hi <= lo + 1e-3:
        return gray
    g = (np.clip(g, lo, hi) - lo) * (255.0 / (hi - lo))
    return g.astype(np.uint8)

def _gamma(gray: np.ndarray, gamma: float) -> np.ndarray:
    """Gamma correction on uint8 grayscale."""
    if gamma <= 0:
        return gray
    inv = 1.0 / gamma
    table = (np.arange(256, dtype=np.float32) / 255.0) ** inv
    table = np.clip(table * 255.0, 0, 255).astype(np.uint8)
    return cv.LUT(gray, table)

def _variant_images(pil_rgb: Image.Image, *, max_w: int = 1920) -> List[Tuple[str, np.ndarray]]:
    """
    Returns list of (variant_name, gray_uint8) images.

    Goal: work for BOTH SDR and HDR screenshots.
    - Avoid hard binarization as the primary path (it can turn ARK UI background into "ink").
    - Prefer raw/contrast/channel variants first; keep binary variants only as fallback.
    """
    im = _cap_width(pil_rgb, max_w=max_w)
    np_rgb = _pil_to_np_rgb(im)  # uint8 RGB
    np_bgr = cv.cvtColor(np_rgb, cv.COLOR_RGB2BGR)

    # Raw grayscale (often best)
    raw = cv.cvtColor(np_rgb, cv.COLOR_RGB2GRAY)

    # Max-RGB (helps colored text on dark UI)
    max_rgb = np.max(np_rgb, axis=2).astype(np.uint8)

    # Percentile normalization (helps HDR/washed captures)
    hdr_norm = _percentile_normalize(raw, 1.0, 99.0)
    if int(np.percentile(raw, 99.0)) - int(np.percentile(raw, 1.0)) < 90:
        hdr_norm = _gamma(hdr_norm, 0.85)

    # CLAHE (good general-purpose)
    clahe = cv.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    clahe_g = clahe.apply(hdr_norm)

    # Red/Magenta emphasis: helps lines rendered in red/magenta (often kill/critical notifications).
    # Idea: emphasize pixels where R or B dominates over G (UI teal background tends to be greenish).
    r = np_rgb[:, :, 0].astype(np.float32)
    gch = np_rgb[:, :, 1].astype(np.float32)
    b = np_rgb[:, :, 2].astype(np.float32)
    rb = np.maximum(r, b)
    rb_minus_g = rb - 0.85 * gch
    rb_minus_g = np.clip(rb_minus_g, 0.0, 255.0).astype(np.uint8)
    rb_minus_g = _percentile_normalize(rb_minus_g, 1.0, 99.0)
    rb_minus_g = clahe.apply(rb_minus_g)
    blur_rb = cv.GaussianBlur(rb_minus_g, (0, 0), 1.0)
    rb_minus_g = cv.addWeighted(rb_minus_g, 1.6, blur_rb, -0.6, 0)

    # Red/magenta mask: isolate saturated red + magenta/pink glyphs.
    # Useful for kill/critical lines rendered in ARK's red/magenta text.
    hsv = cv.cvtColor(np_bgr, cv.COLOR_BGR2HSV)
    h = hsv[:, :, 0]
    s = hsv[:, :, 1]
    v = hsv[:, :, 2]

    # Hue ranges (OpenCV HSV: H 0..179)
    # - Reds wrap around 0
    # - Magenta/pink tends to sit ~140-175 depending on capture/HDR tone-mapping
    red1 = (h <= 12)
    red2 = (h >= 165)
    mag = (h >= 135) & (h <= 175)
    sat = (s >= 70)
    val = (v >= 40)
    m = ((red1 | red2 | mag) & sat & val).astype(np.uint8) * 255

    # Thicken glyphs slightly so OCR has contiguous strokes.
    m = cv.dilate(m, cv.getStructuringElement(cv.MORPH_RECT, (2, 2)), iterations=1)

    # Build a clean OCR input: black text on white background.
    redmag_mask = 255 - m

    # Enhanced (moderate contrast + unsharp)
    g_pil = ImageOps.grayscale(im)
    g_pil = ImageEnhance.Contrast(g_pil).enhance(1.5)
    g_pil = g_pil.filter(ImageFilter.UnsharpMask(radius=1.0, percent=120, threshold=3))
    enhanced = np.asarray(g_pil, dtype=np.uint8)

    # ARK UI (LAB L-channel contrast + unsharp), NO binarization
    lab = cv.cvtColor(np_bgr, cv.COLOR_BGR2LAB)
    l, a, b = cv.split(lab)
    l2 = clahe.apply(l)
    lab2 = cv.merge((l2, a, b))
    bgr2 = cv.cvtColor(lab2, cv.COLOR_LAB2BGR)
    ark_g = cv.cvtColor(bgr2, cv.COLOR_BGR2GRAY)
    blur = cv.GaussianBlur(ark_g, (0, 0), 1.0)
    ark_ui = cv.addWeighted(ark_g, 1.5, blur, -0.5, 0)

    # Binary fallbacks
    bw = _ensure_white_bg(_otsu(clahe_g))
    inv = 255 - bw

    return [
        ("raw", raw),
        ("redmag_mask", redmag_mask),
        ("rb_minus_g", rb_minus_g),
        ("max_rgb", max_rgb),
        ("clahe", clahe_g),
        ("enhanced", enhanced),
        ("hdr_norm", hdr_norm),
        ("ark_ui", ark_ui),
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


def _is_critical_text(s: str) -> bool:
    return bool(_RX_CRITICAL.search(s or ""))


def _critical_hits(lines: List[Line]) -> int:
    hits = 0
    for ln in lines:
        s = (ln.text or "").strip()
        if not s:
            continue
        if _RX_CRITICAL.search(s):
            hits += 1
    return hits


def _norm_line_key(s: str) -> str:
    # Aggressive normalization for cross-variant dedupe (OCR differences, whitespace, punctuation).
    s2 = (s or "").strip().lower()
    s2 = re.sub(r"\s+", " ", s2)
    s2 = re.sub(r"[^a-z0-9:]+", "", s2)
    return s2


_RX_DAYTIME = re.compile(r"^\s*(?:Day|Dav|Doy)\s*[,/:\-]?\s*(\d{1,6})\s*[,/ ]+([0-9]{1,2}:[0-9]{2}:[0-9]{2,3})", re.IGNORECASE)


def _daytime_key(s: str) -> Optional[Tuple[str, str]]:
    m = _RX_DAYTIME.match(s or "")
    if not m:
        return None
    return (m.group(1), m.group(2))


def _fuzzy_event_key(s: str) -> str:
    # Used to avoid dupes when merging multiple OCR variants.
    # Tries to be tolerant of OCR digit noise (coords, ids) while keeping events distinct.
    dt = _daytime_key(s)
    if not dt:
        return _norm_line_key(s)
    day, tm = dt
    # Strip the prefix and then drop digits/punctuation for a fuzzy message fingerprint
    msg = _RX_DAYTIME.sub("", (s or "").lower(), count=1)
    msg = re.sub(r"\d+", "", msg)
    msg = re.sub(r"[^a-z]+", "", msg)
    return f"{day}|{tm}|{msg}"



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
    all_variants = _variant_images(pil, max_w=max_w)

    # Prefer ARK UI suppression first. In fast mode, try only a small fallback set.
    # Ensure red/magenta isolation runs early (it is often where critical events live).
    preferred = [
        "raw",
        "redmag_mask",
        "rb_minus_g",
        "max_rgb",
        "clahe",
        "enhanced",
        "hdr_norm",
        "ark_ui",
        "binary",
        "inverted",
    ]
    variants = list(all_variants)
    variants.sort(key=lambda t: preferred.index(t[0]) if t[0] in preferred else 999)
    if fast:
        try_max = int(os.getenv("OCR_MAX_VARIANTS_FAST", "2"))
        variants = variants[: max(1, min(len(variants), try_max))]

    best: Optional[Dict[str, Any]] = None
    best_key = (-1, -1, -1.0)  # (header_hits, critical_hits, mean_conf)

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
            crit = _critical_hits(lines)
            mc = float(mean_conf(lines))

            candidates.append(
                {
                    "engine": eng,
                    "variant": vname,
                    "header_hits": hits,
                    "critical_hits": crit,
                    "mean_conf": mc,
                    "line_count": len(lines),
                }
            )

            key = (hits, crit, mc)
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
            if fast and (hits >= accept_hits or crit >= 1 or mc >= accept_conf):
                break

        if fast and best is not None and (best_key[0] >= accept_hits or best_key[2] >= accept_conf):
            break

    if best is None:
        return {"engine": "none", "variant": "none", "conf": 0.0, "lines": [], "lines_text": [], "text": "", "candidates": []}

    # Include candidate stats for debugging (/extract UI and logs).
    best["candidates"] = sorted(
        candidates,
        key=lambda d: (d.get("header_hits", 0), d.get("critical_hits", 0), d.get("mean_conf", 0.0)),
        reverse=True,
    )

    # Merge: pull in lines from color-focused variants.
    # Rationale: red + magenta/pink text lines can be under-recognized in the general grayscale variants.
    # We merge only *new* lines (by fuzzy key), and require an ARK "Day ..." header.
    var_map = {k: v for k, v in all_variants}

    def _merge_from(vname: str, *, require_critical: bool) -> int:
        if best.get("variant") == vname:
            return 0
        img = var_map.get(vname)
        if img is None:
            return 0
        try:
            other_lines = _run_engine(best["engine"], img)
        except Exception:
            return 0
        if not other_lines:
            return 0

        seen = {_fuzzy_event_key(d.get("text", "")) for d in best.get("lines", []) if d.get("text")}
        added = 0
        for ln in other_lines:
            s = (ln.text or "").strip()
            if not s:
                continue
            if not _RX_DAY_HEADER.match(s):
                continue
            if require_critical and not _is_critical_text(s):
                continue
            fk = _fuzzy_event_key(s)
            if fk in seen:
                continue
            best["lines"].append({"text": s, "conf": float(ln.conf), "bbox": list(map(int, ln.bbox))})
            seen.add(fk)
            added += 1
        return added

    merged: List[str] = []

    # redmag_mask: merge any new Day-lines (this variant tends to only capture colored glyphs).
    if _env_bool("OCR_MERGE_REDMAG_MASK", default=True):
        if _merge_from("redmag_mask", require_critical=False):
            merged.append("redmag_mask")

    # rb_minus_g: merge only critical lines to avoid adding noise.
    if _env_bool("OCR_MERGE_RB_MINUS_G", default=True):
        if _merge_from("rb_minus_g", require_critical=True):
            merged.append("rb_minus_g")

    if merged:
        best["lines"].sort(key=lambda d: (d["bbox"][1], d["bbox"][0]))
        best["lines_text"] = [d["text"] for d in best["lines"]]
        best["text"] = "\n".join(best["lines_text"])
        best["merged_variants"] = list(dict.fromkeys((best.get("merged_variants") or []) + merged))

    return best
