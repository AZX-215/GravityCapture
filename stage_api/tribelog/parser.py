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
    return out
