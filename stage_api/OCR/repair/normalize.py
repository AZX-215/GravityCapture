import regex as re
from typing import List
from ..schema import Line

TS = re.compile(r'\b(?:\d{1,2}[:/.-]){2}\d{2,4}\b|\b\d{1,2}:\d{2}(?::\d{2})?\b', re.I)
VERBS = {'tamed','killed','destroyed','claimed','demolished','uploaded','downloaded','transferred','removed','added','crafted'}

def schema_score(lines: List[Line]) -> float:
    if not lines:
        return 0.0
    ok = tot = 0
    for ln in lines:
        for tk in ln.text.split():
            tot += 1
            low = tk.lower().strip(".,:;()[]{}")
            if low in VERBS or TS.search(tk):
                ok += 1
    return ok / max(1, tot)

def mean_conf(lines: List[Line]) -> float:
    return sum(ln.conf for ln in lines) / max(1, len(lines))

def normalize(lines: List[Line]) -> List[Line]:
    return lines
