import numpy as np
from typing import List
from rapidocr_onnxruntime import RapidOCR
from ..schema import Line
from .itxt import ITxtExtractor

class PPOCRExtractor(ITxtExtractor):
    def __init__(self):
        # Downloads tiny models on first use; keep one instance
        self.ocr = RapidOCR()

    def run(self, gray_l8: np.ndarray) -> List[Line]:
        bgr = np.stack([gray_l8] * 3, axis=-1)
        boxes, texts, confs = self.ocr(bgr)  # lists or None
        out: List[Line] = []
        for b, t, c in zip(boxes or [], texts or [], confs or []):
            if not t:
                continue
            xs = [int(p[0]) for p in b]
            ys = [int(p[1]) for p in b]
            out.append(Line(text=t, conf=float(c), bbox=(min(xs), min(ys), max(xs), max(ys))))
        return out
