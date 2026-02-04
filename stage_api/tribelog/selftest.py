from __future__ import annotations

import logging
from typing import List

from tribelog.classify import classify_message

logger = logging.getLogger("gravitycapture")


DEFAULT_SELFTEST_LINES: List[str] = [
    # turret kill (previous crash surface)
    "Your Snow Owl - Lvl 286 was killed!",
    "Your Snow Owl - Lvl 286 was killed by Auto Turret!",
    "Your Metal Foundation was auto-decay destroyed!",
    "Your Metal Foundation decayed and was destroyed!",
    "Your Tribe killed Milk - Lvl 277 (Winter Drakeling)!",
    "Your Thor - Lvl 450 (Pyromane) was killed by enemy!",
    "SomePlayer joined the tribe!",
]


def run_classifier_selftest(lines: List[str] | None = None) -> None:
    """Smoke-test classifier rules to catch missing constants/regex at startup.

    This is intentionally simple: it only verifies that classification does not raise.
    """
    test_lines = lines or DEFAULT_SELFTEST_LINES
    for s in test_lines:
        # Should never raise
        classify_message(s)

    logger.info("Classifier self-test passed (%d lines).", len(test_lines))
