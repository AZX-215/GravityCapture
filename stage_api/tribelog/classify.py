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

# Official-ish structure decay line
RX_DECAYED_DESTROYED = re.compile(r"\bdecayed\s+and\s+was\s+destroyed\b", re.I)
RX_YOUR_STRUCT_DECAYED = re.compile(r"^Your\s+(?P<structure>.+?)\s+decayed\s+and\s+was\s+destroyed\s*!?\s*$", re.I)
RX_YOUR_STRUCT_DEMOLISHED = re.compile(r"^Your\s+(?P<structure>.+?)\s+was\s+demolished\s+by\s+(?P<actor>.+?)\s*!?\s*$", re.I)


# Official tribe membership strings ("joined/left the tribe!")
RX_JOIN_LEFT_TRIBE = re.compile(r"^(?P<member>.+?)\s+(?P<action>joined|left)\s+the\s+tribe\s*!?\s*$", re.I)
RX_KICKED_FROM_TRIBE = re.compile(r"^(?P<member>.+?)\s+was\s+kicked\s+from\s+the\s+tribe\s+by\s+(?P<actor>.+?)\s*!?\s*$", re.I)
RX_PROMOTED_DEMOTED = re.compile(r"^(?P<member>.+?)\s+was\s+(?P<action>promoted|demoted)\s+to\s+(?P<rank>.+?)\s*!?\s*$", re.I)
RX_TRIBE_RENAMED = re.compile(r"^\s*Tribe\s+Name\s+was\s+changed\s+to\s+(?P<name>.+?)\s*!?\s*$", re.I)

# Official tame/biology strings
RX_TAMED_OFFICIAL = re.compile(r"^Your\s+Tribe\s+Tamed\s+a\s+(?P<species>.+?)\s+-\s+Lvl\s+(?P<lvl>\d+)\s*!?\s*$", re.I)
RX_CLAIMED_OFFICIAL = re.compile(r"^Your\s+Tribe\s+Claimed\s+a\s+(?P<name>.+?)\s+-\s+Lvl\s+(?P<lvl>\d+)\s*\((?P<species>.+?)\)\s*!?\s*$", re.I)
RX_BIRTH_HATCH = re.compile(r"^A\s+(?P<species>.+?)\s+was\s+(?P<mode>born|hatched)\s*!?\s*$", re.I)
RX_CRYOPOD_RELEASED = re.compile(r"^Your\s+(?P<victim_name>.+?)\s+-\s+Lvl\s+(?P<victim_lvl>\d+)\s*\((?P<victim_species>.+?)\)\s+was\s+released\s+from\s+a\s+Cryopod\s*!?\s*$", re.I)

# Official kill templates (more precise parsing than generic "was killed")
RX_YOUR_DINO_KILLED_BY_PLAYER = re.compile(
    r"^Your\s+(?P<victim_name>.+?)\s+-\s+Lvl\s+(?P<victim_lvl>\d+)\s*\((?P<victim_species>.+?)\)\s+was\s+killed\s+by\s+(?P<attacker_name>.+?)\s+-\s+Lvl\s+(?P<attacker_lvl>\d+)\s*\((?P<attacker_tribe>.+?)\)\s*!?\s*$",
    re.I,
)
RX_YOUR_DINO_KILLED_BY_WILD = re.compile(
    r"^Your\s+(?P<victim_name>.+?)\s+-\s+Lvl\s+(?P<victim_lvl>\d+)\s*\((?P<victim_species>.+?)\)\s+was\s+killed\s+by\s+a\s+(?P<wild_species>.+?)\s+-\s+Lvl\s+(?P<wild_lvl>\d+)\s*!?\s*$",
    re.I,
)
RX_YOUR_DINO_KILLED_ENV = re.compile(
    r"^Your\s+(?P<victim_name>.+?)\s+-\s+Lvl\s+(?P<victim_lvl>\d+)\s*\((?P<victim_species>.+?)\)\s+was\s+killed\s*!?\s*$",
    re.I,
)
RX_PLAYER_KILLED_BY_PLAYER = re.compile(
    r"^(?P<victim_name>.+?)\s+was\s+killed\s+by\s+(?P<attacker_name>.+?)\s+-\s+Lvl\s+(?P<attacker_lvl>\d+)\s*\((?P<attacker_tribe>.+?)\)\s*!?\s*$",
    re.I,
)

# ORP message (unofficial/modded; safe to classify if present)
RX_ORP_PREVENTED = re.compile(r"^\s*An\s+attack\s+was\s+prevented\s+by\s+Offline\s+Raid\s+Protection\s*!?\s*$", re.I)

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
    if RX_AUTO_DECAY.search(m) or RX_DECAYED_DESTROYED.search(m) or RX_YOUR_STRUCT_DECAYED.match(m):
        mdc = RX_YOUR_STRUCT_DECAYED.match(m)
        structure = _clean_entity(mdc.group("structure")) if mdc else ""
        return ("AUTO_DECAY_DESTROYED", "WARNING", structure or "Environment")
    if RX_ANTIMESH.search(m):
        return ("ANTIMESH_DESTROYED", "WARNING", "Environment")

    # Tek Teleporter privacy changed
    mt = RX_TEK_TELEPORTER_PRIVACY.search(m)
    if mt:
        return ("TEK_TELEPORTER_PRIVACY_CHANGED", "WARNING", _clean_actor(mt.group("actor")))

    # ORP message (unofficial/modded)
    if RX_ORP_PREVENTED.match(m):
        return ("ORP_PREVENTED", "INFO", "Environment")

    # Cryopod released (INFO)
    mcr = RX_CRYOPOD_RELEASED.match(m)
    if mcr:
        victim = _clean_entity(mcr.group("victim_name"))
        return ("CRYOPOD_RELEASED", "INFO", victim or "Environment")

    # Birth / hatch (INFO)
    mbh = RX_BIRTH_HATCH.match(m)
    if mbh:
        species = _clean_entity(mbh.group("species"))
        return ("BIRTH_HATCHED", "INFO", species or "Environment")

    # Official tame success (SUCCESS)
    mto = RX_TAMED_OFFICIAL.match(m)
    if mto:
        species = _clean_entity(mto.group("species"))
        return ("TAME_TAMED", "SUCCESS", species or "Your Tribe")

    # Official claiming (SUCCESS)
    mco = RX_CLAIMED_OFFICIAL.match(m)
    if mco:
        name = _clean_entity(mco.group("name"))
        return ("TAME_CLAIMED", "SUCCESS", name or "Your Tribe")

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
    mjl = RX_JOIN_LEFT_TRIBE.match(m)
    if mjl:
        member = _clean_entity(mjl.group("member"))
        action = (mjl.group("action") or "").strip().lower()
        if action == "joined":
            return ("TRIBE_MEMBER_ADDED", "INFO", member or "Environment")
        return ("TRIBE_MEMBER_LEFT", "INFO", member or "Environment")

    mk = RX_KICKED_FROM_TRIBE.match(m)
    if mk:
        member = _clean_entity(mk.group("member"))
        actor = _clean_actor(mk.group("actor"))
        return ("TRIBE_MEMBER_KICKED", "WARNING", actor or member or "Environment")

    mpd = RX_PROMOTED_DEMOTED.match(m)
    if mpd:
        member = _clean_entity(mpd.group("member"))
        return ("TRIBE_RANK_CHANGED", "INFO", member or "Environment")

    mrn = RX_TRIBE_RENAMED.match(m)
    if mrn:
        name = _clean_entity(mrn.group("name"))
        return ("TRIBE_RENAMED", "INFO", name or "Environment")

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
    myd = RX_YOUR_STRUCT_DEMOLISHED.match(m)
    if myd:
        return ("STRUCTURE_DEMOLISHED", "INFO", _clean_actor(myd.group("actor")) or "Environment")

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

    # Player killed by player/tame (official template without "Tribemember" prefix)
    mpk = RX_PLAYER_KILLED_BY_PLAYER.match(m)
    if mpk:
        victim = _clean_entity(mpk.group("victim_name"))
        # Guard: if the victim looks like a dino template, let the dino patterns handle it.
        if not re.search(r"\s-\s*Lvl\s+\d+\s*\(", victim, re.I):
            actor = _clean_actor(mpk.group("attacker_name"))
            return ("TRIBEMEMBER_WAS_KILLED", "CRITICAL", actor or victim or "Environment")

    # Your tame killed by enemy player/tame
    mkp = RX_YOUR_DINO_KILLED_BY_PLAYER.match(m)
    if mkp:
        actor = _clean_actor(mkp.group("attacker_name"))
        victim_name = _clean_entity(mkp.group("victim_name"))
        return ("TAME_DIED", "CRITICAL", actor or victim_name or "Environment")

    # Your tame killed by wild creature
    mkw = RX_YOUR_DINO_KILLED_BY_WILD.match(m)
    if mkw:
        wild = _clean_entity(mkw.group("wild_species"))
        victim_name = _clean_entity(mkw.group("victim_name"))
        return ("TAME_DIED", "CRITICAL", (wild or "Environment") if wild else (victim_name or "Environment"))

    # Your tame killed without explicit attacker (environment / mesh / drowning / etc.)
    mke = RX_YOUR_DINO_KILLED_ENV.match(m)
    if mke:
        victim_name = _clean_entity(mke.group("victim_name"))
        # Treat as non-attributed death => WARNING (per requested policy)
        return ("TAME_DIED", "WARNING", victim_name or "Environment")

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
