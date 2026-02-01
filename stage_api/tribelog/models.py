from __future__ import annotations

from dataclasses import dataclass
from typing import Optional


@dataclass(frozen=True)
class ParsedEvent:
    server: str
    tribe: str
    ark_day: int
    ark_time: str  # HH:MM:SS
    severity: str  # INFO | WARNING | SUCCESS | CRITICAL
    category: str  # e.g., STRUCTURE_DESTROYED
    actor: str
    message: str
    raw_line: str
    event_hash: str

    # v2 dedupe / normalization (optional; safe defaults)
    event_hash_v2: Optional[str] = None
    normalized_text: Optional[str] = None
    fingerprint: Optional[int] = None
