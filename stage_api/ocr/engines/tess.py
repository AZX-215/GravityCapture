import os
from dataclasses import dataclass
from typing import Dict, List, Tuple

import numpy as np

import pytesseract
from pytesseract import Output  # type: ignore

from ..schema import Line
from .itxt import ITxtExtractor

# Allow override on Windows (desktop dev)
if os.name == "nt":
    tpath = os.getenv("TESSERACT_PATH")
    if tpath and os.path.exists(tpath):
        pytesseract.pytesseract.tesseract_cmd = tpath

# Keep whitelist permissive; tribe logs include punctuation and apostrophes.
WHITELIST = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789:/()#._- '!,?+[]"

def _cfg(psm: int = 6) -> str:
    # psm 6 = assume a single uniform block of text. We then regroup tokens -> real lines.
    # Disable dictionary lookups (often hurts game/UI text) and keep spaces.
    return (
        f'--oem 1 --psm {psm} '
        f'-c tessedit_char_whitelist="{WHITELIST}" '
        f'-c preserve_interword_spaces=1 '
        f'-c load_system_dawg=0 -c load_freq_dawg=0'
    )

def _safe_float(x) -> float:
    try:
        return float(x)
    except Exception:
        return float("nan")

def _group_tokens(data: Dict[str, List], min_conf: float = 0.0) -> List[Line]:
    """
    Group image_to_data tokens back into lines using (block_num, par_num, line_num).
    Returns Line objects with text, mean conf, and line bbox.
    """
    n = len(data.get("text", []))
    groups: Dict[Tuple[int,int,int], List[int]] = {}

    for i in range(n):
        txt = (data["text"][i] or "").strip()
        if not txt:
            continue
        conf_raw = _safe_float(data.get("conf", ["-1"])[i])
        if np.isnan(conf_raw) or conf_raw < 0:
            continue
        conf = conf_raw / 100.0
        if conf < min_conf:
            continue

        key = (
            int(data.get("block_num", [0])[i]),
            int(data.get("par_num", [0])[i]),
            int(data.get("line_num", [0])[i]),
        )
        groups.setdefault(key, []).append(i)

    out: List[Line] = []
    for key, idxs in groups.items():
        # preserve token order left->right
        idxs_sorted = sorted(idxs, key=lambda j: int(data.get("left", [0])[j]))
        toks = [str(data["text"][j]).strip() for j in idxs_sorted if str(data["text"][j]).strip()]
        if not toks:
            continue

        # bbox over tokens
        lefts = [int(data["left"][j]) for j in idxs_sorted]
        tops = [int(data["top"][j]) for j in idxs_sorted]
        rights = [int(data["left"][j]) + int(data["width"][j]) for j in idxs_sorted]
        bottoms = [int(data["top"][j]) + int(data["height"][j]) for j in idxs_sorted]
        x0, y0, x1, y1 = min(lefts), min(tops), max(rights), max(bottoms)

        confs = []
        for j in idxs_sorted:
            c = _safe_float(data.get("conf", ["-1"])[j])
            if not np.isnan(c) and c >= 0:
                confs.append(c / 100.0)
        mean_c = float(sum(confs) / len(confs)) if confs else 0.0

        text = " ".join(toks)
        out.append(Line(text=text, conf=mean_c, bbox=(x0, y0, x1, y1)))

    # Sort lines top->bottom, then left->right
    out.sort(key=lambda ln: (ln.bbox[1], ln.bbox[0]))
    return out

class TesseractExtractor(ITxtExtractor):
    """
    Robust line extraction for the ARK tribe-log panel.

    IMPORTANT:
    - Do NOT binarize/split by horizontal projection. That approach turns ARK's UI background into "ink"
      and creates garbage line bands.
    - Instead, run Tesseract once (psm=6) and regroup tokens into real lines.
    """

    def run(self, gray_l8: np.ndarray) -> List[Line]:
        assert gray_l8.ndim == 2, "expect grayscale (H,W)"

        # Tesseract expects uint8
        g = gray_l8.astype(np.uint8, copy=False)

        data = pytesseract.image_to_data(g, output_type=Output.DICT, config=_cfg(psm=6))
        return _group_tokens(data, min_conf=0.0)
