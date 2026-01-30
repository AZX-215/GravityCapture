from dataclasses import dataclass
from typing import List, Tuple

BBox = Tuple[int, int, int, int]  # x0,y0,x1,y1

@dataclass
class Line:
    text: str
    conf: float
    bbox: BBox

@dataclass
class OcrResult:
    engine: str           # "tesseract" | future engines
    conf: float           # mean confidence
    lines: List[Line]
