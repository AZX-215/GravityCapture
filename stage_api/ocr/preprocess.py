from io import BytesIO
import numpy as np
from PIL import Image, ImageOps, ImageFilter

def _otsu_threshold(gray_np: np.ndarray) -> int:
    hist, _ = np.histogram(gray_np.flatten(), bins=256, range=(0, 256))
    total = gray_np.size
    sum_total = np.dot(np.arange(256), hist)
    sum_b = 0.0
    w_b = 0.0
    max_var = 0.0
    threshold = 127
    for t in range(256):
        w_b += hist[t]
        if w_b == 0:
            continue
        w_f = total - w_b
        if w_f == 0:
            break
        sum_b += t * hist[t]
        m_b = sum_b / w_b
        m_f = (sum_total - sum_b) / w_f
        var_between = w_b * w_f * (m_b - m_f) ** 2
        if var_between > max_var:
            max_var = var_between
            threshold = t
    return threshold

def load_and_preprocess(image_bytes: bytes) -> Image.Image:
    im = Image.open(BytesIO(image_bytes)).convert("RGB")
    im = ImageOps.autocontrast(im, cutoff=1)
    max_w = 1920
    if im.width > max_w:
        h = int(im.height * (max_w / im.width))
        im = im.resize((max_w, h), Image.LANCZOS)
    im = im.filter(ImageFilter.UnsharpMask(radius=1.2, percent=130, threshold=3))
    gray = im.convert("L")
    g = np.asarray(gray, dtype=np.uint8)
    t = _otsu_threshold(g)
    bw = (g > t).astype(np.uint8) * 255
    return Image.fromarray(bw, mode="L")
