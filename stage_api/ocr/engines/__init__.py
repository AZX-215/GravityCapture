from typing import Optional
from .itxt import ITxtExtractor
from .tess import TesseractExtractor
try:
    from .ppocr import PPOCRExtractor  # optional
except Exception:  # pragma: no cover
    PPOCRExtractor = None  # type: ignore

def make_extractor(name: Optional[str]) -> ITxtExtractor:
    """
    Factory. Supported names:
      - 'tesseract' (default)
      - 'ppocr' / 'rapidocr' / 'paddle'  (requires rapidocr_onnxruntime)
    """
    n = (name or "tesseract").strip().lower()
    if n in ("ppocr", "rapidocr", "paddle"):
        if PPOCRExtractor is None:
            raise RuntimeError("PPOCR engine not available")
        return PPOCRExtractor()
    return TesseractExtractor()

__all__ = [
    "ITxtExtractor",
    "TesseractExtractor",
    "PPOCRExtractor",
    "make_extractor",
]
