from __future__ import annotations

from dataclasses import dataclass


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
