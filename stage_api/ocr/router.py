from typing import Literal, Dict, Any
from PIL import Image
from .preprocess import load_and_preprocess
from .rapidocr_engine import run_rapidocr
from .tesseract_engine import run_tesseract

EngineHint = Literal["auto", "ppo", "tess"]

def extract_text(image_bytes: bytes, engine_hint: EngineHint = "auto") -> Dict[str, Any]:
    pre = load_and_preprocess(image_bytes)

    if engine_hint == "ppo":
        return run_rapidocr(pre)
    if engine_hint == "tess":
        return run_tesseract(pre)

    # auto
    res = run_rapidocr(pre)
    if res.get("conf", 0.45) < 0.45 or not res.get("lines"):
        res = run_tesseract(pre)
    return res
