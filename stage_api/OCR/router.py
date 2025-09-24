import numpy as np
from .preprocess import preprocess_auto
from .engines.ppocr import PPOCRExtractor
from .engines.tess import TesseractExtractor
from .repair.normalize import schema_score, mean_conf, normalize
from .schema import OcrResult

_PP = PPOCRExtractor()
_TS = TesseractExtractor()

def extract_text(img_bgr: np.ndarray, engine_pref: str = "auto",
                 conf_thr: float = 0.80, schema_thr: float = 0.05) -> OcrResult:
    g = preprocess_auto(img_bgr)
    cands = {}

    if engine_pref in ("auto", "ppo"):
        L = _PP.run(g)
        cands["ppo"] = (L, mean_conf(L), schema_score(L))

    use_tess = engine_pref in ("auto", "tess")
    if engine_pref == "auto":
        ok = False
        if "ppo" in cands:
            _, c, s = cands["ppo"]
            ok = (c >= conf_thr and s >= schema_thr)
        use_tess = not ok

    if use_tess:
        L = _TS.run(g)
        cands["tess"] = (L, mean_conf(L), schema_score(L))

    best, bestv = None, -1.0
    for k, (L, c, s) in cands.items():
        v = 0.7 * c + 0.3 * s
        if v > bestv:
            best, bestv = k, v

    lines, c, _ = cands[best]
    lines = normalize(lines)
    return OcrResult(engine=best, conf=c, lines=lines)
