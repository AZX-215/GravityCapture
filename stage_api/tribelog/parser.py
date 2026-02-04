from __future__ import annotations

import re
from typing import Dict, List, Optional, Tuple


# Tolerant tribe-log header:
# - comma after day is optional
# - colon after time is optional
# - separators can be :, . or whitespace (OCR)
# - seconds may be 2 or 3 digits (OCR can produce 122)
# - "Day" can be misread as "Dav"/"Doy"
_RX_HEADER = re.compile(
    r"""
    ^\s*
    (?:(?:Day|Dav|Doy))\s*[,/:\-]?\s*
    (?P<day>\d{1,6})
    (?:\s*,\s*|\s+)?
    (?P<hour>\d{1,2})
    \s*[:.]\s*
    (?P<minute>\d{1,2})
    (?:\s*[:.]\s*(?P<second>\d{2,3}))?
    \s*(?:\s*[:\-]\s*|\s+)?   # optional ':' before message
    (?P<msg>.*?)
    \s*$
    """,
    re.IGNORECASE | re.VERBOSE,
)


# Same header pattern, but not anchored. Used to split lines where OCR concatenates multiple events.
_RX_HEADER_ANY = re.compile(
    r"(?:Day|Dav|Doy)\s*[,/:\-]?\s*\d{1,6}(?:\s*,\s*|\s+)?\d{1,2}\s*[:.]\s*\d{1,2}(?:\s*[:.]\s*\d{2,3})?",
    re.IGNORECASE,
)


def _clamp_int(x: int, lo: int, hi: int) -> int:
    return max(lo, min(hi, x))


def _parse_int(s: Optional[str], default: int = 0) -> int:
    try:
        return int((s or "").strip())
    except Exception:
        return default


def _normalize_time(hour_s: str, minute_s: str, second_s: Optional[str]) -> str:
    hh = _clamp_int(_parse_int(hour_s, 0), 0, 23)
    mm = _clamp_int(_parse_int(minute_s, 0), 0, 59)

    if second_s is None or not str(second_s).strip():
        ss = 0
    else:
        sec_raw = str(second_s).strip()
        # If OCR outputs 3 digits (e.g. 122), keep the last 2 digits.
        if len(sec_raw) >= 3:
            sec_raw = sec_raw[-2:]
        ss = _clamp_int(_parse_int(sec_raw, 0), 0, 59)

    return f"{hh:02d}:{mm:02d}:{ss:02d}"


def stitch_wrapped_lines(lines: List[str]) -> List[str]:
    """
    Tribe log events are one-per-line but OCR can wrap them.
    Strategy:
      - Start a new line when we see a header match
      - Otherwise append to the previous line
    """
    out: List[str] = []

    def _split_multi_headers(line: str) -> List[str]:
        s2 = (line or "").strip()
        if not s2:
            return []
        ms = list(_RX_HEADER_ANY.finditer(s2))
        if not ms:
            return [s2]
        # If the first header isn't at the start, keep the prefix to append to the previous line.
        parts: List[str] = []
        if ms[0].start() > 0:
            prefix = s2[: ms[0].start()].strip()
            if prefix:
                parts.append(prefix)  # marked as non-header prefix
        for i, m in enumerate(ms):
            start = m.start()
            end = ms[i + 1].start() if i + 1 < len(ms) else len(s2)
            parts.append(s2[start:end].strip())
        return [p for p in parts if p]

    for raw in lines or []:
        s = (raw or "").strip()
        if not s:
            continue
        # Skip pure punctuation/noise so we don't append "-" onto valid headers.
        if re.fullmatch(r"[-–—_.]+", s):
            continue

        # If OCR concatenated multiple events into one "line", split them back out.
        parts = _split_multi_headers(s)
        if not parts:
            continue

        for p in parts:
            if not p:
                continue
            if _RX_HEADER.match(p):
                out.append(p)
                continue
            # Non-header prefix: append to previous header line if possible.
            if out:
                out[-1] = (out[-1] + " " + p).strip()
            else:
                out.append(p)

    return out


def parse_header_lines(lines: List[str]) -> List[Dict[str, object]]:
    """
    Returns normalized header lines:
      { ark_day:int, ark_time:str, message:str, raw_line:str }
    """
    out: List[Dict[str, object]] = []
    for s in lines or []:
        m = _RX_HEADER.match((s or "").strip())
        if not m:
            continue

        day = _parse_int(m.group("day"), 0)
        if day <= 0:
            continue

        ark_time = _normalize_time(m.group("hour"), m.group("minute"), m.group("second"))
        msg = (m.group("msg") or "").strip()
        raw_one = re.sub(r"\s+", " ", s).strip()

        out.append(
            {
                "ark_day": day,
                "ark_time": ark_time,
                "message": msg,
                "raw_line": raw_one,
            }
        )

    out = _merge_starved_killed_pairs(out)
    out = _merge_same_timestamp_fragments(out)
    out = _drop_noise_events(out)
    out = _drop_fragment_substrings(out)
    return out


def _canonical_victim(s: str) -> str:
    """Make a victim key stable across OCR variations (e.g. leading 'Your')."""
    v = re.sub(r"\s+", " ", (s or "").strip())
    v = re.sub(r"^Your\s+", "", v, flags=re.IGNORECASE)
    v = v.strip(" !.\t\r\n")
    return v


def _extract_victim_from_starved(msg: str) -> Optional[str]:
    m = re.match(r"^(?P<v>.+?)\s+starved\s+to\s+death!?$", (msg or "").strip(), flags=re.IGNORECASE)
    if not m:
        return None
    return _canonical_victim(m.group("v"))


def _extract_victim_from_killed(msg: str) -> Optional[str]:
    """Only the 'was killed' lines without an explicit killer are eligible for merge."""
    s = (msg or "").strip()
    if not re.search(r"\bwas\s+killed\b", s, flags=re.IGNORECASE):
        return None
    # If there is an explicit killer ("was killed by ..."), do not merge.
    if re.search(r"\bwas\s+killed\s+by\b", s, flags=re.IGNORECASE):
        return None
    m = re.match(r"^(?P<v>.+?)\s+was\s+killed\b.*$", s, flags=re.IGNORECASE)
    if not m:
        return None
    return _canonical_victim(m.group("v"))


def _merge_starved_killed_pairs(events: List[Dict[str, object]]) -> List[Dict[str, object]]:
    """
    ARK often emits two lines with identical timestamps for starvation:
      1) "<Creature> starved to death!"
      2) "Your <Creature> was killed!"

    Treat those as ONE event (the starved line). We drop the paired kill line.
    """
    if not events:
        return events

    starved_keys = set()
    for e in events:
        victim = _extract_victim_from_starved(str(e.get("message") or ""))
        if not victim:
            continue
        key = (int(e.get("ark_day") or 0), str(e.get("ark_time") or ""), victim)
        starved_keys.add(key)

    out: List[Dict[str, object]] = []
    for e in events:
        msg = str(e.get("message") or "")
        victim = _extract_victim_from_killed(msg)
        if victim:
            key = (int(e.get("ark_day") or 0), str(e.get("ark_time") or ""), victim)
            if key in starved_keys:
                # drop the redundant kill line
                continue
        out.append(e)
    return out


_ACTION_KWS = (
    "was killed",
    "was destroyed",
    "was demolished",
    "decayed and was destroyed",
    "starved to death",
    "destroyed their",
    "tamed a",
    "claimed a",
    "was born",
    "hatched",
    "joined the tribe",
    "left the tribe",
    "was kicked",
    "uploaded",
    "downloaded",
    "was released from a cryopod",
    "froze",
    "unfroze",
)


def _has_action_keywords(msg: str) -> bool:
    m = (msg or "").lower()
    return any(k in m for k in _ACTION_KWS)


def _looks_like_continuation(msg: str) -> bool:
    s = (msg or "").strip()
    if not s:
        return False
    if s[0] in "([{'\"":
        return True
    if s.startswith("-"):
        return True
    if re.match(r"^(?:Lvl\b|-\s*Lvl\b|\d+\b)", s, flags=re.IGNORECASE):
        return True
    return False


def _looks_like_fragment(msg: str) -> bool:
    s = (msg or "").strip()
    if not s:
        return True
    if len(s) <= 2:
        return True
    if s in {"-", "—", "–", "_"}:
        return True
    if re.fullmatch(r"[-–—_.]+", s):
        return True
    if len(s) < 20 and not _has_action_keywords(s):
        return True
    if re.search(r"(?:\bLvl\b|\bwas\b|\bby\b|\bTribe\b)\s*$", s, flags=re.IGNORECASE):
        return True
    if s.endswith("-"):
        return True
    return False


def _merge_same_timestamp_fragments(events: List[Dict[str, object]]) -> List[Dict[str, object]]:
    """Merge consecutive same-timestamp entries when one/both look like wrapped fragments."""
    out: List[Dict[str, object]] = []

    for e in events or []:
        if not out:
            out.append(e)
            continue

        prev = out[-1]
        same_ts = (
            int(prev.get("ark_day") or 0) == int(e.get("ark_day") or 0)
            and str(prev.get("ark_time") or "") == str(e.get("ark_time") or "")
        )
        if not same_ts:
            out.append(e)
            continue

        prev_msg = str(prev.get("message") or "").strip()
        cur_msg = str(e.get("message") or "").strip()
        if not cur_msg:
            continue

        if _looks_like_fragment(prev_msg) or _looks_like_continuation(cur_msg):
            prev["message"] = (prev_msg + " " + cur_msg).strip()

            prev_raw = str(prev.get("raw_line") or "").strip()
            cur_raw = str(e.get("raw_line") or "").strip()
            if cur_raw and cur_raw not in prev_raw:
                prev["raw_line"] = (prev_raw + " | " + cur_raw).strip(" |")
            continue

        if _looks_like_fragment(cur_msg) and not _has_action_keywords(prev_msg):
            prev["message"] = (prev_msg + " " + cur_msg).strip()
            continue

        out.append(e)

    return out


def _is_noise_message(msg: str) -> bool:
    s = (msg or "").strip()
    if not s:
        return True
    if s in {"-", "—", "–", "_"}:
        return True
    if re.fullmatch(r"[-–—_.]+", s):
        return True
    if re.fullmatch(r"\d{1,2}[:.,]\d{2}(?:[:.,]\d{2})?", s):
        return True
    return False


def _drop_noise_events(events: List[Dict[str, object]]) -> List[Dict[str, object]]:
    out: List[Dict[str, object]] = []
    for e in events or []:
        if _is_noise_message(str(e.get("message") or "")):
            continue
        out.append(e)
    return out


def _norm_cmp(s: str) -> str:
    s = (s or "").lower()
    s = re.sub(r"\s+", " ", s).strip()
    s = re.sub(r"[\"'`]+", "", s)
    return s


def _drop_fragment_substrings(events: List[Dict[str, object]]) -> List[Dict[str, object]]:
    """Drop fragment-only entries when the adjacent same-timestamp entry contains the full text."""
    if not events:
        return []

    out: List[Dict[str, object]] = []
    i = 0
    while i < len(events):
        cur = events[i]
        cur_msg = str(cur.get("message") or "").strip()
        if i + 1 < len(events):
            nxt = events[i + 1]
            same_ts = (
                int(cur.get("ark_day") or 0) == int(nxt.get("ark_day") or 0)
                and str(cur.get("ark_time") or "") == str(nxt.get("ark_time") or "")
            )
            if same_ts:
                a = _norm_cmp(cur_msg)
                b = _norm_cmp(str(nxt.get("message") or "").strip())
                if a and b and a != b and a in b and _looks_like_fragment(cur_msg) and not _has_action_keywords(cur_msg):
                    i += 1
                    continue
        out.append(cur)
        i += 1
    return out
