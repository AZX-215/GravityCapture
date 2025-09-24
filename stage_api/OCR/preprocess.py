import cv2 as cv
import numpy as np

def preprocess_auto(img_bgr: np.ndarray, max_w: int = 1200) -> np.ndarray:
    """Resize, percentile stretch, CLAHE. Return uint8 grayscale."""
    h, w = img_bgr.shape[:2]
    if w > max_w:
        s = max_w / float(w)
        img_bgr = cv.resize(img_bgr, (max_w, int(h * s)), interpolation=cv.INTER_AREA)
    gray = cv.cvtColor(img_bgr, cv.COLOR_BGR2GRAY)
    lo, hi = np.percentile(gray, (0.7, 99.3))
    if hi > lo:
        x = ((gray.astype(np.float32) - lo) / (hi - lo)).clip(0, 1)
        gray = (x * 255).astype(np.uint8)
    clahe = cv.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    return clahe.apply(gray)
