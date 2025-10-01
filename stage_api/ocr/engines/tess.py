import os
from typing import List
import numpy as np

# OpenCV and Tesseract
import cv2 as cv
import pytesseract
from pytesseract import Output  # type: ignore

from ..schema import Line
from .itxt import ITxtExtractor

# Allow override on Windows
if os.name == "nt":
    tpath = os.getenv("TESSERACT_PATH")
    if tpath and os.path.exists(tpath):
        pytesseract.pytesseract.tesseract_cmd = tpath

WHITELIST = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789:/()#._- '!,?"

def _binarize(g: np.ndarray) -> np.ndarray:
    # Adaptive threshold + small dilation to connect glyph strokes
    bw = cv.adaptiveThreshold(g, 255, cv.ADAPTIVE_THRESH_GAUSSIAN_C,
                              cv.THRESH_BINARY, 41, 12)
    if bw.mean() < 127:
        bw = 255 - bw
    se = cv.getStructuringElement(cv.MORPH_RECT, (3, 1))
    return cv.dilate(bw, se, 1)

def _cfg(psm: int = 7) -> str:
    return f'--oem 1 --psm {psm} -c tessedit_char_whitelist="{WHITELIST}"'

class TesseractExtractor(ITxtExtractor):
    """
    Lightweight line-wise extractor using Tesseract.
    Splits by horizontal projection, then OCR per strip using psm=7 (single line).
    """

    def run(self, gray_l8: np.ndarray) -> List[Line]:
        assert gray_l8.ndim == 2, "expect grayscale (H,W)"
        bw = _binarize(gray_l8)

        # Horizontal projection to find line bands
        hproj = (bw < 128).sum(axis=1)
        thr = max(1, int(0.05 * bw.shape[1]))  # 5% of width with ink
        out: List[Line] = []

        run, y0 = False, 0
        for y, v in enumerate(hproj.tolist() + [0]):  # sentinel to flush last line
            if v > thr and not run:
                run, y0 = True, y
            elif v <= thr and run:
                run = False
                y1 = y
                if y1 - y0 < 8:
                    continue

                strip = bw[y0:y1, :]
                xs = np.where((strip < 128).sum(axis=0) > 0)[0]
                if xs.size == 0:
                    continue
                x0, x1 = int(xs.min()), int(xs.max())
                roi = strip[:, x0:x1]

                # Use data API for basic confidence
                data = pytesseract.image_to_data(roi, output_type=Output.DICT, config=_cfg(psm=7))
                tokens = [t.strip() for t in data.get("text", []) if t and t.strip()]
                if not tokens:
                    continue
                txt = " ".join(tokens)

                confs = []
                for c in data.get("conf", []):
                    try:
                        cval = float(c)
                        if cval >= 0:
                            confs.append(cval / 100.0)
                    except Exception:
                        pass
                conf = float(sum(confs) / len(confs)) if confs else 0.70

                out.append(Line(text=txt, conf=conf, bbox=(x0, y0, x1, y1)))

        return out
