import os
import regex as re
from typing import Dict, Iterable, List, Set, Tuple
from ..schema import Line

# Timestamp-like tokens (e.g., 12:34, 12:34:56, 01/02/2025, 01-02-25)
TS = re.compile(
    r"\b(?:\d{1,2}[:/.\-]){2}\d{2,4}\b|\b\d{1,2}:\d{2}(?::\d{2})?\b",
    re.I,
)

# Expanded verb lexicon for ARK tribe logs
VERBS: Set[str] = {
    "tamed", "killed", "destroyed", "claimed", "demolished",
    "uploaded", "downloaded", "transferred", "removed", "added", "crafted",
    "died", "dies", "die",
    "starved", "starving", "starve",
    "decayed", "auto-decayed", "autodecayed", "decay", "auto-decay",
    "froze", "frozen", "freezing", "freeze",
    "demolish", "demolishing",
    "attacked", "attacking",
    "killed-by", "destroyed-by",
    "auto-decays", "auto-decaying",
}

# ---------- ARK lexicons (creatures, structures, vehicles) ----------
def _read_list(path: str) -> List[str]:
    try:
        with open(path, "r", encoding="utf-8") as f:
            return [ln.strip() for ln in f if ln.strip()]
    except Exception:
        return []

_here = os.path.dirname(__file__)
CREATURES = _read_list(os.path.join(_here, "Ark_Creatures.txt"))
STRUCTURES = _read_list(os.path.join(_here, "Ark_Structures.txt"))
VEHICLES  = _read_list(os.path.join(_here, "Ark_Vehicle.txt"))

def _canon_key(s: str) -> str:
    return re.sub(r"[\s\-]+", " ", s.strip().lower())

def _to_pattern(name: str) -> str:
    parts = [re.escape(p) for p in re.split(r"\s+", name.strip()) if p]
    if not parts:
        return r"$^"
    return r"\b" + r"[\s\-]*".join(parts) + r"\b"

def _build_lexicon(names: Iterable[str]):
    canon: Dict[str, str] = {}
    pats: List[str] = []
    for nm in sorted(set(names), key=len, reverse=True):
        if not nm:
            continue
        key = _canon_key(nm)
        if key not in canon:
            canon[key] = nm
            pats.append(_to_pattern(nm))
    pat = re.compile("|".join(pats), re.I) if pats else re.compile(r"$^")
    return canon, pat

CRE_CANON, CRE_PAT = _build_lexicon(CREATURES)
STR_CANON, STR_PAT = _build_lexicon(STRUCTURES)
VEH_CANON, VEH_PAT = _build_lexicon(VEHICLES)

def load_lexicons():
    """For diagnostics/tests."""
    return set(CRE_CANON.values()), set(STR_CANON.values()), set(VEH_CANON.values())

# ---------- OCR noise repair ----------
DIGIT_GLUE_FIX = (
    (re.compile(r"(?<=\d)O(?=\d)"), "0"),
    (re.compile(r"(?<=\d)[lI](?=\d)"), "1"),
)
DASH_FIX = (
    (re.compile(r"[—–]+"), "-"),
    (re.compile(r"\s*-\s*"), " - "),
)
SPACE_FIX = (
    (re.compile(r"\s+"), " "),
    (re.compile(r"\s([:;,.])"), r"\1"),
)

def _repair_text(t: str) -> str:
    if not t:
        return t
    out = t.replace("“", '"').replace("”", '"').replace("’", "'").replace("‘", "'")
    for rx, rep in DIGIT_GLUE_FIX:
        out = rx.sub(rep, out)
    for rx, rep in DASH_FIX:
        out = rx.sub(rep, out)
    for rx, rep in SPACE_FIX:
        out = rx.sub(rep, out)
    return out.strip()

def _canon_replace(text: str, canon: Dict[str, str], pat: re.Pattern) -> str:
    def _sub(m: re.Match) -> str:
        key = _canon_key(m.group(0))
        return canon.get(key, m.group(0))
    return pat.sub(_sub, text)

# ---------- scoring and normalization ----------
def schema_score(lines: List[Line]) -> float:
    """Heuristic: fraction of tokens that look like verbs, timestamps, or ARK entities."""
    if not lines:
        return 0.0
    ok = tot = 0
    for ln in lines:
        for tk in ln.text.split():
            tot += 1
            low = tk.lower().strip(".,:;()[]{}")
            if (
                low in VERBS
                or TS.search(tk) is not None
                or CRE_PAT.search(tk) is not None
                or STR_PAT.search(tk) is not None
                or VEH_PAT.search(tk) is not None
            ):
                ok += 1
    return ok / max(1, tot)

def mean_conf(lines: List[Line]) -> float:
    return sum(ln.conf for ln in lines) / max(1, len(lines))

def _normalize_line_text(text: str) -> str:
    txt = _repair_text(text)
    txt = _canon_replace(txt, CRE_CANON, CRE_PAT)
    txt = _canon_replace(txt, STR_CANON, STR_PAT)
    txt = _canon_replace(txt, VEH_CANON, VEH_PAT)
    # normalize common verb renderings
    txt = re.sub(r"\bkilled[\s\-]*by\b", "killed by", txt, flags=re.I)
    txt = re.sub(r"\bdestroyed[\s\-]*by\b", "destroyed by", txt, flags=re.I)
    txt = re.sub(r"\bauto[\s\-]*decay(ing|s)?\b", "auto-decayed", txt, flags=re.I)
    txt = re.sub(r"\bstarving\b", "starved", txt, flags=re.I)
    txt = re.sub(r"\bdies\b", "died", txt, flags=re.I)
    txt = re.sub(r"\bdemolishing\b", "demolished", txt, flags=re.I)
    txt = re.sub(r"\bfreezing\b", "froze", txt, flags=re.I)
    return re.sub(r"\s{2,}", " ", txt).strip()

def normalize(lines: List[Line]) -> List[Line]:
    """Return new list with repaired/normalized text per line; bbox and conf preserved."""
    out: List[Line] = []
    for ln in lines:
        try:
            new_text = _normalize_line_text(ln.text or "")
        except Exception:
            new_text = ln.text or ""
        out.append(Line(text=new_text, conf=ln.conf, bbox=ln.bbox))
    return out
