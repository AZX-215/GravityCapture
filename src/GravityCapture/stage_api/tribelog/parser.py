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
    for raw in lines or []:
        s = (raw or "").strip()
        if not s:
            continue
        if _RX_HEADER.match(s):
            out.append(s)
        else:
            if out:
                out[-1] = (out[-1] + " " + s).strip()
            else:
                out.append(s)
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

    return _merge_starved_killed_pairs(out)


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
