import cv2 as cv
import numpy as np
import pytesseract
from typing import List
from ..schema import Line
from .itxt import ITxtExtractor

WHITELIST = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789:/()#._- "

def _binarize(g: np.ndarray) -> np.ndarray:
    bw = cv.adaptiveThreshold(g, 255, cv.ADAPTIVE_THRESH_GAUSSIAN_C,
                              cv.THRESH_BINARY, 41, 12)
    if bw.mean() < 127:
        bw = 255 - bw
    se = cv.getStructuringElement(cv.MORPH_RECT, (3, 1))
    return cv.dilate(bw, se, 1)

def _cfg() -> str:
    return f'--oem 1 --psm 7 -c tessedit_char_whitelist="{WHITELIST}"'

class TesseractExtractor(ITxtExtractor):
    def run(self, gray_l8: np.ndarray) -> List[Line]:
        bw = _binarize(gray_l8)
        hproj = (bw < 128).sum(axis=1)
        thr = max(1, int(0.05 * bw.shape[1]))
        out: List[Line] = []
        run, y0 = False, 0
        for y, v in enumerate(hproj.tolist() + [0]):
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
                txt = pytesseract.image_to_string(roi, config=_cfg()).strip()
                if txt:
                    out.append(Line(text=txt, conf=0.70, bbox=(x0, y0, x1, y1)))
        return out
