from __future__ import annotations

import regex as re
from typing import Tuple

from db import compute_event_hash
from tribelog.models import ParsedEvent

# ---- helpers ----

def _clean_actor(s: str) -> str:
    s = (s or "").strip()
    if not s:
        return ""
    # remove trailing "(...)" like "(C4)"
    s = re.sub(r"\s*\([^)]*\)\s*$", "", s)
    # trim trailing punctuation/spaces
    s = re.sub(r"[.!:,;\s]+$", "", s)
    return s.strip()


def _norm_spaces(s: str) -> str:
    return re.sub(r"\s+", " ", (s or "").strip())


# ---- patterns (case-insensitive) ----

RX_AUTO_DECAY = re.compile(r"\bauto[-\s]?decay\b.*\bdestroyed\b|\bauto[-\s]?decay\b\s+destroyed\b", re.I)
RX_ANTIMESH = re.compile(r"\banti[-\s]?mesh\b|\bmesh\b.*\bdestroyed\b", re.I)

RX_DESTROYED_BY = re.compile(
    r"\bwas\b\s+destroyed\b\s+by\s+(?P<actor>.+?)(?:\s*\(|!|\.|$)",
    re.I,
)
RX_DESTROYED = re.compile(r"\bwas\b\s+destroyed\b", re.I)

RX_DEMOLISHED = re.compile(r"\b(?P<actor>[\w\p{L}\p{N}_ ]+?)\s+demolished\b", re.I)

RX_TRIBE_KILLED = re.compile(r"\b(Your\s+Tribe|Human)\s+killed\b\s+(?P<victim>.+)$", re.I)
RX_WAS_KILLED = re.compile(r"\bwas\b\s+killed\b", re.I)

RX_CRYOPOD = re.compile(r"\bcryopod\b", re.I)
RX_STARVED = re.compile(r"\bstarv(?:ed|ing|e)\b", re.I)
RX_FROZE = re.compile(r"\bfro(?:ze|zen|ze to death)\b", re.I)

RX_TAMED = re.compile(r"\b(Your\s+Tribe|Human)\s+Tamed\s+a\b", re.I)
RX_CLAIMED = re.compile(r"\b(Human|Your\s+Tribe)\s+claimed\b", re.I)
RX_UNCLAIMED = re.compile(r"\bunclaimed\b", re.I)

RX_JOINED = re.compile(r"\bjoined\b.*\btribe\b", re.I)
RX_LEFT = re.compile(r"\bleft\b.*\btribe\b", re.I)

RX_SENSOR = re.compile(r"\b(tek\s+sensor|parasaur)\b.*\b(detect|ping)\b", re.I)

RX_UNFROZE = re.compile(r"\bunfroze\b", re.I)
RX_HATCHED = re.compile(r"\bhas\s+been\s+hatched\b", re.I)
RX_BORN = re.compile(r"\bhas\s+been\s+born\b", re.I)
RX_UPLOADED = re.compile(r"\buploaded\b", re.I)
RX_DOWNLOADED = re.compile(r"\bdownloaded\b", re.I)
RX_TRANSFERRED = re.compile(r"\btransferred\b", re.I)
RX_ONLINE = re.compile(r"\b(now\s+online|came\s+online)\b", re.I)
RX_OFFLINE = re.compile(r"\b(went\s+offline|gone\s+offline|now\s+offline)\b", re.I)
RX_TRIBE_NAME_CHANGED = re.compile(r"\btribe\s+name\s+changed\b", re.I)
RX_TRIBE_OWNER_CHANGED = re.compile(r"\btribe\s+owner\s+changed\b", re.I)
RX_SET_RANK = re.compile(r"\bset\s+the\s+rank\b|\bset\s+rank\b", re.I)
RX_ADDED_TO_TRIBE = re.compile(r"\bwas\s+added\s+to\s+the\s+tribe\b", re.I)
RX_REMOVED_FROM_TRIBE = re.compile(r"\bwas\s+removed\s+from\s+the\s+tribe\b", re.I)

RX_ENEMY_DESTROYED = re.compile(r"\bdestroyed\b\s+their\b", re.I)


def classify_message(msg: str) -> Tuple[str, str, str]:
    """
    Returns (category, severity, actor)
    """
    m = _norm_spaces(msg)

    # Auto decay destroyed (should be WARNING, not CRITICAL)
    if RX_AUTO_DECAY.search(m):
        return ("AUTO_DECAY_DESTROYED", "WARNING", "Auto Decay")

    # Anti-mesh destroyed
    if RX_ANTIMESH.search(m):
        return ("ANTIMESH_DESTROYED", "WARNING", "Anti-mesh")

    # Your tribe killed ...
    mk = RX_TRIBE_KILLED.search(m)
    if mk:
        victim = _clean_actor(mk.group("victim"))
        # Player kill heuristic: contains "Human" or "player" token
        if re.search(r"\bHuman\b|\bplayer\b", victim, re.I):
            return ("TRIBE_KILLED_PLAYER", "CRITICAL", victim)
        return ("TRIBE_KILLED_CREATURE", "INFO", victim)

    # Tamed
    if RX_TAMED.search(m):
        return ("TAME_TAMED", "SUCCESS", "")

    # Claimed / Unclaimed
    if RX_CLAIMED.search(m):
        return ("TAME_CLAIMED", "INFO", "")
    if RX_UNCLAIMED.search(m):
        return ("TAME_UNCLAIMED", "INFO", "")


    # Baby / hatch events
    if RX_HATCHED.search(m):
        return ("EGG_HATCHED", "SUCCESS", "")
    if RX_BORN.search(m):
        return ("BABY_BORN", "SUCCESS", "")

    # Upload / download / transfers (Obelisk / terminals / etc.)
    if RX_UPLOADED.search(m):
        return ("UPLOADED", "INFO", "")
    if RX_DOWNLOADED.search(m):
        return ("DOWNLOADED", "INFO", "")
    if RX_TRANSFERRED.search(m):
        return ("TRANSFERRED", "INFO", "")

    # Tribe status changes
    if RX_TRIBE_NAME_CHANGED.search(m):
        return ("TRIBE_NAME_CHANGED", "INFO", "")
    if RX_TRIBE_OWNER_CHANGED.search(m):
        return ("TRIBE_OWNER_CHANGED", "INFO", "")
    if RX_SET_RANK.search(m):
        return ("TRIBE_RANK_CHANGED", "INFO", "")
    if RX_ONLINE.search(m):
        return ("TRIBE_MEMBER_ONLINE", "INFO", "")
    if RX_OFFLINE.search(m):
        return ("TRIBE_MEMBER_OFFLINE", "INFO", "")

    # Explicit add/remove messages
    if RX_ADDED_TO_TRIBE.search(m):
        return ("TRIBE_MEMBER_JOINED", "INFO", "")
    if RX_REMOVED_FROM_TRIBE.search(m):
        return ("TRIBE_MEMBER_LEFT", "INFO", "")

    # Unfroze (sometimes appears in cryo/thaw messages)
    if RX_UNFROZE.search(m):
        return ("TAME_UNFROZE", "INFO", "")
    # Sensor / parasaur
    if RX_SENSOR.search(m):
        return ("SENSOR_ALERT", "WARNING", "")

    # Demolished
    md = RX_DEMOLISHED.search(m)
    if md:
        return ("STRUCTURE_DEMOLISHED", "INFO", _clean_actor(md.group("actor")))

    # Destroyed their (enemy structure destroyed)
    if RX_ENEMY_DESTROYED.search(m):
        return ("ENEMY_STRUCTURE_DESTROYED", "SUCCESS", "")

    # Was destroyed / destroyed by
    if RX_DESTROYED.search(m):
        mb = RX_DESTROYED_BY.search(m)
        actor = _clean_actor(mb.group("actor")) if mb else ""
        return ("STRUCTURE_DESTROYED", "CRITICAL", actor)

    # Was killed
    if RX_WAS_KILLED.search(m):
        # Cryopod death
        if RX_CRYOPOD.search(m):
            return ("CRYOPOD_DEATH", "WARNING", "Cryopod")
        # Starved/froze
        if RX_STARVED.search(m):
            return ("TAME_STARVED", "WARNING", "")
        if RX_FROZE.search(m):
            return ("TAME_FROZE", "WARNING", "")
        # If killed by Human -> CRITICAL
        if re.search(r"\bby\b\s+Human\b", m, re.I):
            return ("TAME_KILLED_BY_HUMAN", "CRITICAL", "Human")
        return ("TAME_DIED", "WARNING", "")

    # Tribe membership changes
    if RX_JOINED.search(m):
        return ("TRIBE_MEMBER_JOINED", "INFO", "")
    if RX_LEFT.search(m):
        return ("TRIBE_MEMBER_LEFT", "INFO", "")

    return ("UNKNOWN", "INFO", "")


def classify_event(
    *,
    server: str,
    tribe: str,
    ark_day: int,
    ark_time: str,
    message: str,
    raw_line: str,
) -> ParsedEvent:
    category, severity, actor = classify_message(message)

    # Ensure actor is not huge
    actor = _clean_actor(actor)

    # Stable hash used for dedupe
    h = compute_event_hash(
        server=server,
        tribe=tribe,
        ark_day=int(ark_day),
        ark_time=str(ark_time),
        category=category,
        message=message,
    )

    return ParsedEvent(
        server=server,
        tribe=tribe,
        ark_day=int(ark_day),
        ark_time=str(ark_time),
        severity=severity,
        category=category,
        actor=actor,
        message=_norm_spaces(message),
        raw_line=_norm_spaces(raw_line),
        event_hash=h,
    )