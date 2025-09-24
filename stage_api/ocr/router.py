from typing import Dict, Any
from .preprocess import load_and_preprocess
from .tesseract_engine import run_tesseract

def extract_text(image_bytes: bytes, engine_hint: str = "auto") -> Dict[str, Any]:
    # Preprocess once
    pre = load_and_preprocess(image_bytes)
    # Only Tesseract right now
    return run_tesseract(pre)
