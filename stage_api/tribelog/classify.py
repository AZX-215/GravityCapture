from __future__ import annotations

import os
import regex as re
from typing import Tuple

from db import compute_event_hash, compute_event_hash_v2, compute_fingerprint64, normalize_event_text
from tribelog.models import ParsedEvent


# -----------------
# Helpers
# -----------------


def _norm_spaces(s: str) -> str:
    return re.sub(r"\s+", " ", (s or "").strip())


def _strip_trailing_punct(s: str) -> str:
    return re.sub(r"[.!:,;\s]+$", "", (s or "").strip()).strip()


def _clean_entity(s: str) -> str:
    s = _norm_spaces(s)
    s = re.sub(r"^Your\s+", "", s, flags=re.I)
    s = _strip_trailing_punct(s)
    return s


def _clean_actor(s: str) -> str:
    s = _norm_spaces(s)
    # remove trailing "(...)" like "(C4)" or "(Clone)" when it's clearly an annotation
    s = re.sub(r"\s*\([^)]*\)\s*$", "", s)
    s = _strip_trailing_punct(s)
    return s


# -----------------
# Patterns (tuned for ARK tribe logs)
# -----------------

RX_AUTO_DECAY = re.compile(r"\bauto[-\s]?decay\b.*\b(destroyed|decay destroyed)\b", re.I)
RX_ANTIMESH = re.compile(r"\banti[-\s]?mesh\b|\bmesh\b.*\bdestroyed\b", re.I)

# Structures
RX_DEMOLISHED = re.compile(r"^(?P<actor>.+?)\s+demolished\b", re.I)
RX_DESTROYED_BY = re.compile(r"\bwas\s+destroyed\s+by\s+(?P<actor>.+?)\s*(?:!|\.|$)", re.I)
RX_DESTROYED = re.compile(r"\bwas\s+destroyed\b", re.I)
RX_ENEMY_DESTROYED = re.compile(r"\bdestroyed\b\s+their\b", re.I)

# Kills
RX_TRIBEMEMBER_KILLED_BY = re.compile(
    r"^\s*Tribemember\s+(?P<victim>.+?)\s+was\s+killed\s+by\s+(?P<actor>.+?)\s*!?\s*$",
    re.I,
)
RX_YOUR_TRIBE_KILLED = re.compile(r"^\s*Your\s+Tribe\s+killed\s+(?P<victim>.+?)\s*!?\s*$", re.I)
RX_WAS_KILLED_BY = re.compile(r"^(?P<victim>.+?)\s+was\s+killed\s+by\s+(?P<actor>.+?)\s*!?\s*$", re.I)
RX_WAS_KILLED = re.compile(r"^(?P<victim>.+?)\s+was\s+killed\b", re.I)

# Tames
RX_STARVED = re.compile(r"^(?P<victim>.+?)\s+starved\s+to\s+death\s*!?\s*$", re.I)
RX_FROZE = re.compile(r"^(?P<actor>.+?)\s+froze\s+(?P<victim>.+?)\s*$", re.I)
RX_CRYOPOD = re.compile(r"\bcryopod\b", re.I)

RX_CLAIMED = re.compile(r"^(?P<actor>.+?)\s+claimed\s+(?P<what>.+?)\s*!?\s*$", re.I)
RX_UNCLAIMED = re.compile(r"\bunclaimed\b", re.I)
RX_TAMED = re.compile(r"\btamed\s+a\b", re.I)

# Transfers
RX_UPLOADED = re.compile(r"^(?P<actor>.+?)\s+uploaded\b", re.I)
RX_DOWNLOADED = re.compile(r"^(?P<actor>.+?)\s+downloaded\b", re.I)
RX_TRANSFERRED = re.compile(r"\btransferred\b", re.I)

# Tribe membership / roles
RX_ADDED_TO_TRIBE = re.compile(
    r"^(?P<member>.+?)\s+was\s+added\s+to\s+the\s+Tribe\s+by\s+(?P<actor>.+?)\s*!?\s*$",
    re.I,
)
RX_LEFT_TRIBE = re.compile(r"^(?P<member>.+?)\s+left\s+the\s+Tribe\s*!?\s*$", re.I)
RX_REMOVED_FROM_TRIBE = re.compile(
    r"^(?P<member>.+?)\s+was\s+removed\s+from\s+the\s+Tribe(?:\s+by\s+(?P<actor>.+?))?\s*!?\s*$",
    re.I,
)
RX_PROMOTED_ADMIN = re.compile(
    r"^(?P<member>.+?)\s+was\s+promoted\s+to\s+a\s+Tribe\s+Admin\s+by\s+(?P<actor>.+?)\s*!?\s*$",
    re.I,
)
RX_OWNER_CHANGED = re.compile(r"^\s*Tribe\s+Owner\s+was\s+changed\s+to\s+(?P<new_owner>.+?)\s*!?\s*$", re.I)
RX_SET_RANK_GROUP = re.compile(r"\bset\s+to\s+Rank\s+Group\b.*\bby\b\s+(?P<actor>.+?)\s*!?\s*$", re.I)

# Tek Teleporter privacy
RX_TEK_TELEPORTER_PRIVACY = re.compile(
    r"^(?P<actor>.+?)\s+set\s+\(\d+\)\s+.+?\(\s*Small\s+Tek\s+Teleporter\s*\)\s+to\s+(?P<mode>public|private)\b",
    re.I,
)



def _env_bool(name: str, default: bool = False) -> bool:
    v = os.getenv(name)
    if v is None:
        return default
    v = str(v).strip().lower()
    return v in {"1", "true", "yes", "y", "on", "enable", "enabled"}


def _get_csv(name: str, default_csv: str) -> Tuple[str, ...]:
    raw = os.getenv(name)
    s = (raw if raw is not None else default_csv) or ""
    parts = [p.strip().lower() for p in s.split(",") if p.strip()]
    return tuple(dict.fromkeys(parts))


def _contains_any(haystack: str, needles: Tuple[str, ...]) -> bool:
    h = (haystack or "").lower()
    return any(n and n in h for n in needles)

def _is_probably_player(victim: str) -> bool:
    """Heuristic only; used to keep legacy categories."""
    v = victim or ""
    # Player kills often show "Name - Lvl 123 (Tribe - Name)"
    if re.search(r"\([^)]*\-\s*[^)]*\)", v):
        return True
    return False


def classify_message(msg: str) -> Tuple[str, str, str]:
    """Returns (category, severity, actor)."""

    m = _norm_spaces(msg)

    # --- WARNING (non-combat / environment) ---
    if RX_AUTO_DECAY.search(m):
        return ("AUTO_DECAY_DESTROYED", "WARNING", "Environment")
    if RX_ANTIMESH.search(m):
        return ("ANTIMESH_DESTROYED", "WARNING", "Environment")

    # Tek Teleporter privacy changed
    mt = RX_TEK_TELEPORTER_PRIVACY.search(m)
    if mt:
        return ("TEK_TELEPORTER_PRIVACY_CHANGED", "WARNING", _clean_actor(mt.group("actor")))

    # Starved to death (WARNING; actor is the creature that starved)
    ms = RX_STARVED.match(m)
    if ms:
        victim = _clean_entity(ms.group("victim"))
        return ("TAME_STARVED", "WARNING", victim or "Environment")

    # --- INFO / SUCCESS ---
    # Froze (INFO; actor is the player/creature doing the freezing)
    mf = RX_FROZE.match(m)
    if mf:
        return ("TAME_FROZE", "INFO", _clean_actor(mf.group("actor")) or "Environment")

    # Claimed (SUCCESS)
    mc = RX_CLAIMED.match(m)
    if mc:
        return ("TAME_CLAIMED", "SUCCESS", _clean_actor(mc.group("actor")) or "Environment")
    if RX_UNCLAIMED.search(m):
        return ("TAME_UNCLAIMED", "INFO", "Environment")

    # Tamed
    if RX_TAMED.search(m):
        return ("TAME_TAMED", "SUCCESS", "Environment")

    # Upload / download / transfers
    mu = RX_UPLOADED.match(m)
    if mu:
        return ("UPLOADED", "INFO", _clean_actor(mu.group("actor")) or "Environment")
    md = RX_DOWNLOADED.match(m)
    if md:
        return ("DOWNLOADED", "INFO", _clean_actor(md.group("actor")) or "Environment")
    if RX_TRANSFERRED.search(m):
        return ("TRANSFERRED", "INFO", "Environment")

    # Tribe membership / roles
    ma = RX_ADDED_TO_TRIBE.match(m)
    if ma:
        return ("TRIBE_MEMBER_ADDED", "INFO", _clean_actor(ma.group("actor")) or _clean_entity(ma.group("member")) or "Environment")

    ml = RX_LEFT_TRIBE.match(m)
    if ml:
        member = _clean_entity(ml.group("member"))
        return ("TRIBE_MEMBER_LEFT", "INFO", member or "Environment")

    mr = RX_REMOVED_FROM_TRIBE.match(m)
    if mr:
        member = _clean_entity(mr.group("member"))
        actor = _clean_actor(mr.group("actor") or "")
        return ("TRIBE_MEMBER_REMOVED", "CRITICAL", actor or member or "Environment")

    mp = RX_PROMOTED_ADMIN.match(m)
    if mp:
        return ("TRIBE_RANK_CHANGED", "INFO", _clean_actor(mp.group("actor")) or _clean_entity(mp.group("member")) or "Environment")

    mo = RX_OWNER_CHANGED.match(m)
    if mo:
        new_owner = _clean_entity(mo.group("new_owner"))
        # No explicit actor in the log line; per rule use the target name.
        return ("TRIBE_OWNERSHIP_CHANGED", "CRITICAL", new_owner or "Environment")

    mg = RX_SET_RANK_GROUP.search(m)
    if mg:
        return ("TRIBE_RANK_CHANGED", "INFO", _clean_actor(mg.group("actor")) or "Environment")

    # --- STRUCTURES ---
    mm = RX_DEMOLISHED.match(m)
    if mm:
        return ("STRUCTURE_DEMOLISHED", "INFO", _clean_actor(mm.group("actor")) or "Environment")

    if RX_ENEMY_DESTROYED.search(m):
        return ("ENEMY_STRUCTURE_DESTROYED", "SUCCESS", "Environment")

    if RX_DESTROYED.search(m):
        mb = RX_DESTROYED_BY.search(m)
        actor = _clean_actor(mb.group("actor")) if mb else "Environment"

        # Default behavior (back-compat): STRUCTURE_DESTROYED is CRITICAL.
        sev = "CRITICAL"
        if _env_bool("CLASSIFY_TIERED_STRUCTURE_SEVERITY", default=False):
            crit_kw = _get_csv(
                "CLASSIFY_CRITICAL_STRUCT_KEYWORDS",
                "tek,vault,generator,replicator,teleporter,transmitter,turret,fridge,cryofridge",
            )
            sev = "CRITICAL" if _contains_any(m, crit_kw) else "WARNING"

        return ("STRUCTURE_DESTROYED", sev, actor or "Environment")

    # --- KILLS (CRITICAL) ---
    tm = RX_TRIBEMEMBER_KILLED_BY.match(m)
    if tm:
        return ("TRIBEMEMBER_WAS_KILLED", "CRITICAL", _clean_actor(tm.group("actor")) or _clean_entity(tm.group("victim")) or "Environment")

    yt = RX_YOUR_TRIBE_KILLED.match(m)
    if yt:
        victim = _clean_entity(yt.group("victim"))
        # Keep legacy split categories, but make both CRITICAL.
        cat = "TRIBE_KILLED_PLAYER" if _is_probably_player(victim) else "TRIBE_KILLED_CREATURE"
        return (cat, "CRITICAL", "Your Tribe")

    wk = RX_WAS_KILLED_BY.match(m)
    if wk:
        victim = _clean_entity(wk.group("victim"))
        actor = _clean_actor(wk.group("actor"))
        # If the line is about a tame/player being killed, the killer is the actor.
        return ("TAME_DIED", "CRITICAL", actor or victim or "Environment")

    if RX_WAS_KILLED.search(m):
        # Cryopod death is not a combat kill.
        if RX_CRYOPOD.search(m):
            return ("CRYOPOD_DEATH", "WARNING", "Environment")

        # No explicit killer => usually starvation, drowning, antimesh, etc.
        vm = RX_WAS_KILLED.match(m)
        victim = _clean_entity(vm.group("victim")) if vm else ""

        # If OCR merged starvation context into the same line, treat as starvation.
        if re.search(r"\bstarved\b", m, re.I):
            return ("TAME_STARVED", "WARNING", victim or "Environment")

        # Environmental / unknown-cause deaths should not be CRITICAL.
        return ("TAME_DIED", "WARNING", victim or "Environment")


    # Fallback
    return ("UNKNOWN", "INFO", "Environment")


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

    actor = _clean_actor(actor) or "Environment"

    # Stable hash used for dedupe
    h = compute_event_hash(
        server=server,
        tribe=tribe,
        ark_day=int(ark_day),
        ark_time=str(ark_time),
        category=category,
        message=message,
    )

    # v2: more stable under OCR drift (uses actor+message normalization)
    h2 = compute_event_hash_v2(
        server=server,
        tribe=tribe,
        ark_day=int(ark_day),
        ark_time=str(ark_time),
        category=category,
        actor=actor,
        message=message,
    )
    norm_text = normalize_event_text(f"{category} {actor} {message}")
    fp = compute_fingerprint64(norm_text)

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
        event_hash_v2=h2,
        normalized_text=norm_text,
        fingerprint=fp,
    )
