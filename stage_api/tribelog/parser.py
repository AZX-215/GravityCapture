from __future__ import annotations

import re
from typing import Dict, List


# Header line, supports wrapped lines by stitching first.
_RX_HEADER = re.compile(
    r"^\s*Day\s*(?P<day>\d+)\s*,\s*(?P<time>\d{1,2}:\d{2}:\d{2})\s*:\s*(?P<msg>.*)\s*$",
    re.IGNORECASE,
)


def stitch_wrapped_lines(lines: List[str]) -> List[str]:
    """
    Tribe log events are one-per-line but OCR can wrap them.
    Strategy:
      - Start a new line when we see a Day header
      - Otherwise, append to the previous line
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
        m = _RX_HEADER.match(s)
        if not m:
            continue
        day = int(m.group("day"))
        t = m.group("time").strip()
        msg = (m.group("msg") or "").strip()
        raw_one = re.sub(r"\s+", " ", s).strip()
        out.append(
            {
                "ark_day": day,
                "ark_time": t,
                "message": msg,
                "raw_line": raw_one,
            }
        )
    return out
