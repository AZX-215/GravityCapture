import os
from typing import List, Dict, Any
from PIL import Image
import pytesseract

# Optional explicit tesseract path on Windows
if os.name == "nt":
    tpath = os.getenv("TESSERACT_PATH")
    if tpath and os.path.exists(tpath):
        pytesseract.pytesseract.tesseract_cmd = tpath

def run_tesseract(pil_img: Image.Image) -> Dict[str, Any]:
    data = pytesseract.image_to_data(
        pil_img,
        output_type=pytesseract.Output.DICT,
        config="--oem 3 --psm 6",
    )
    lines: List[Dict[str, Any]] = []
    confs: List[float] = []

    n = len(data.get("text", []))
    for i in range(n):
        text = (data["text"][i] or "").strip()
        if not text:
            continue
        try:
            conf = float(data["conf"][i]) / 100.0
        except Exception:
            conf = 0.0
        x, y = int(data["left"][i]), int(data["top"][i])
        w, h = int(data["width"][i]), int(data["height"][i])
        lines.append({"text": text, "conf": conf, "bbox": [x, y, x + w, y + h]})
        confs.append(conf)

    conf_avg = float(sum(confs) / len(confs)) if confs else 0.0
    return {"engine": "tesseract", "conf": conf_avg, "lines": lines}
