from typing import Dict, Any, List
from .preprocess import load_and_preprocess
from .tesseract_engine import run_tesseract

def _join_lines(lines: List[Dict[str, Any]]) -> str:
    # Tesseract returns token-level words. Join with spaces and normalize.
    parts = [str(item.get("text", "")).strip() for item in lines if item.get("text")]
    text = " ".join(parts)
    # light cleanup for doubled spaces
    return " ".join(text.split())

def extract_text(image_bytes: bytes, engine_hint: str = "auto") -> Dict[str, Any]:
    """
    High-level OCR entry point used by the API.
    For now we only run Tesseract after preprocessing.
    Returns a dict with keys: engine, conf, lines, text
    """
    pre = load_and_preprocess(image_bytes)
    res = run_tesseract(pre)  # {engine, conf, lines}
    lines = res.get("lines", [])
    return {
        "engine": res.get("engine", "tesseract"),
        "conf": res.get("conf", 0.0),
        "lines": lines,
        "text": _join_lines(lines),
    }
