from __future__ import annotations

from io import BytesIO
from typing import Any, Dict, List, Optional, Tuple

import numpy as np
from PIL import Image, ImageEnhance, ImageFilter, ImageOps, ImageFile

from .engines import make_extractor, PPOCRExtractor  # type: ignore
from .schema import Line
from .repair.normalize import normalize, schema_score, mean_conf

ImageFile.LOAD_TRUNCATED_IMAGES = True


def _otsu_threshold(gray_np: np.ndarray) -> int:
    hist, _ = np.histogram(gray_np.flatten(), bins=256, range=(0, 256))
    total = gray_np.size
    sum_total = float(np.dot(np.arange(256), hist))
    sum_b = 0.0
    w_b = 0.0
    max_var = 0.0
    threshold = 127
    for t in range(256):
        w_b += float(hist[t])
        if w_b == 0:
            continue
        w_f = float(total) - w_b
        if w_f == 0:
            break
        sum_b += float(t * hist[t])
        m_b = sum_b / w_b
        m_f = (sum_total - sum_b) / w_f
        var_between = w_b * w_f * (m_b - m_f) ** 2
        if var_between > max_var:
            max_var = var_between
            threshold = t
    return int(threshold)


def _load_pil(image_bytes: bytes) -> Image.Image:
    return Image.open(BytesIO(image_bytes)).convert("RGB")


def _variant_images(pil_rgb: Image.Image) -> List[Tuple[str, Image.Image]]:
    # Common resize cap: wide screenshots can be very large; cap width for speed/stability.
    max_w = 1920
    im = pil_rgb
    if im.width > max_w:
        h = int(im.height * (max_w / im.width))
        im = im.resize((max_w, h), Image.LANCZOS)

    # Variant A: enhanced grayscale (good for most logs)
    g = ImageOps.grayscale(im)
    g = ImageEnhance.Contrast(g).enhance(1.8)
    g = g.filter(ImageFilter.UnsharpMask(radius=1.2, percent=140, threshold=3))

    # Variant B: binary (Otsu)
    g_np = np.asarray(g, dtype=np.uint8)
    t = _otsu_threshold(g_np)
    bw = (g_np > t).astype(np.uint8) * 255
    bw_img = Image.fromarray(bw, mode="L")

    # Variant C: inverted binary (sometimes HDR inverts the log)
    inv_img = ImageOps.invert(bw_img)

    return [
        ("enhanced", g),
        ("binary", bw_img),
        ("inverted", inv_img),
    ]


def _pil_to_gray_np(pil_img: Image.Image) -> np.ndarray:
    # engines expect grayscale uint8
    g = pil_img.convert("L")
    return np.asarray(g, dtype=np.uint8)


def _run_engine(engine_name: str, gray_np: np.ndarray) -> List[Line]:
    ext = make_extractor(engine_name)
    lines = ext.run(gray_np)  # List[Line]
    # Normalize line text (repairs common ARK OCR artifacts)
    return normalize(lines)


def _joined_text(lines: List[Line]) -> str:
    return "\n".join([ln.text.strip() for ln in lines if ln.text and ln.text.strip()])


def extract_text(image_bytes: bytes, engine_hint: str = "auto") -> Dict[str, Any]:
    """
    High-level OCR entry point used by the API.
    Returns: {engine, variant, conf, lines, lines_text, text}
    """
    pil = _load_pil(image_bytes)

    engines: List[str]
    hint = (engine_hint or "auto").strip().lower()
    if hint in ("ppocr", "rapidocr", "paddle"):
        engines = ["ppocr"]
    elif hint in ("tess", "tesseract"):
        engines = ["tesseract"]
    else:
        # Auto: try ppocr first (if available) and fall back to tesseract.
        engines = ["ppocr", "tesseract"]

    best: Optional[Dict[str, Any]] = None
    best_score = -1.0

    variants = _variant_images(pil)

    for vname, vim in variants:
        gray_np = _pil_to_gray_np(vim)
        for eng in engines:
            try:
                lines = _run_engine(eng, gray_np)
            except Exception:
                continue
            if not lines:
                continue

            # Scoring: schema_score (prefers "Day <n>, <time>:") + mean confidence
            sc = float(schema_score(lines)) + float(mean_conf(lines)) * 0.5
            txt = _joined_text(lines)

            # Extra bump when multiple Day headers appear
            sc += min(2.0, txt.lower().count("day ") * 0.25)

            if sc > best_score:
                best_score = sc
                best = {
                    "engine": eng,
                    "variant": vname,
                    "conf": float(mean_conf(lines)),
                    "lines": [{"text": ln.text, "conf": float(ln.conf), "bbox": list(ln.bbox)} for ln in lines],
                    "lines_text": [ln.text for ln in lines if ln.text and ln.text.strip()],
                    "text": txt,
                }

    if best is None:
        return {"engine": "none", "variant": "none", "conf": 0.0, "lines": [], "lines_text": [], "text": ""}

    return best
