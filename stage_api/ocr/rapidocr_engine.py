import numpy as np
from PIL import Image
from rapidocr_onnxruntime import RapidOCR

_rapid = RapidOCR()

def run_rapidocr(pil_img: Image.Image):
    arr = np.asarray(pil_img.convert("RGB"))
    boxes, texts, confs = _rapid(arr)
    lines = []
    conf_list = []
    for b, t, c in zip(boxes or [], texts or [], confs or []):
        if not t:
            continue
        xs = [int(p[0]) for p in b]
        ys = [int(p[1]) for p in b]
        lines.append({"text": t, "conf": float(c), "bbox": [min(xs), min(ys), max(xs), max(ys)]})
        conf_list.append(float(c))
    conf = float(np.mean(conf_list)) if conf_list else 0.0
    return {"engine": "ppo", "conf": conf, "lines": lines}
