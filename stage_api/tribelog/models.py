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

    # v1: legacy (kept for back-compat)
    event_hash: str

    # v2: more stable under OCR drift (additive; safe to ignore if unused)
    event_hash_v2: str = ""
    normalized_text: str = ""
    fingerprint: int = 0
