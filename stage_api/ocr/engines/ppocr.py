from typing import List, Optional
import numpy as np
from ..schema import Line
from .itxt import ITxtExtractor

class PPOCRExtractor(ITxtExtractor):
    """
    RapidOCR (ONNXRuntime) wrapper.
    Lazy-initializes to avoid import costs if engine not used.
    """

    def __init__(self) -> None:
        self._ocr = None  # type: Optional["RapidOCR"]

    def _ensure(self) -> "RapidOCR":
        if self._ocr is None:
            from rapidocr_onnxruntime import RapidOCR  # heavy import
            self._ocr = RapidOCR()
        return self._ocr

    def run(self, gray_l8: np.ndarray) -> List[Line]:
        assert gray_l8.ndim == 2, "expect grayscale (H,W)"
        bgr = np.stack([gray_l8] * 3, axis=-1)  # H,W,3
        boxes, texts, confs = self._ensure()(bgr)  # lists or None

        out: List[Line] = []
        for b, t, c in zip(boxes or [], texts or [], confs or []):
            if not t:
                continue
            xs = [int(p[0]) for p in b]
            ys = [int(p[1]) for p in b]
            out.append(
                Line(text=str(t), conf=float(c), bbox=(min(xs), min(ys), max(xs), max(ys)))
            )
        return out
