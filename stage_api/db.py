from __future__ import annotations

import hashlib
from dataclasses import dataclass
from typing import Iterable, List, Optional, Set

import asyncpg

from tribelog.models import ParsedEvent


# -----------------------------
# Schema
# -----------------------------
_CREATE_TRIBE_EVENTS_TABLE = """
CREATE TABLE IF NOT EXISTS tribe_events (
  id BIGSERIAL PRIMARY KEY,
  ingested_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  tenant_id BIGINT,
  server TEXT NOT NULL,
  tribe TEXT NOT NULL,
  ark_day INTEGER NOT NULL,
  ark_time TEXT NOT NULL,
  severity TEXT NOT NULL,
  category TEXT NOT NULL,
  actor TEXT NOT NULL,
  message TEXT NOT NULL,
  raw_line TEXT NOT NULL,
  event_hash TEXT
);
"""

_CREATE_TENANTS_TABLE = """
CREATE TABLE IF NOT EXISTS tenants (
  id BIGSERIAL PRIMARY KEY,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  name TEXT NOT NULL UNIQUE,
  api_key_hash TEXT NOT NULL UNIQUE,
  webhook_url TEXT NOT NULL DEFAULT '',
  is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
  log_posting_enabled BOOLEAN NOT NULL DEFAULT TRUE,
  post_delay_seconds DOUBLE PRECISION NOT NULL DEFAULT 0.8,
  critical_ping_enabled BOOLEAN NOT NULL DEFAULT TRUE,
  critical_ping_role_id TEXT NOT NULL DEFAULT '',
  ping_all_critical BOOLEAN NOT NULL DEFAULT FALSE,
  ping_categories TEXT NOT NULL DEFAULT ''
);
"""

# Legacy/old indexes
_DROP_LEGACY_EVENT_HASH_UQ = "DROP INDEX IF EXISTS tribe_events_event_hash_uq;"
_DROP_LEGACY_RAW_LINE_UQ = "DROP INDEX IF EXISTS tribe_events_raw_line_uidx;"

# Current indexes
_CREATE_TENANT_EVENT_HASH_UQ = (
    "CREATE UNIQUE INDEX IF NOT EXISTS tribe_events_tenant_event_hash_uq "
    "ON tribe_events (tenant_id, event_hash) WHERE event_hash IS NOT NULL;"
)
_CREATE_INGESTED_IDX = "CREATE INDEX IF NOT EXISTS tribe_events_ingested_at_idx ON tribe_events (ingested_at);"
_CREATE_TENANT_ID_IDX = "CREATE INDEX IF NOT EXISTS tribe_events_tenant_id_idx ON tribe_events (tenant_id);"


# -----------------------------
# Helpers
# -----------------------------
def compute_event_hash(
    *,
    server: str,
    tribe: str,
    ark_day: int,
    ark_time: str,
    category: str,
    message: str,
) -> str:
    """Stable and resistant to OCR spacing noise."""
    norm = "|".join(
        [
            (server or "").strip().lower(),
            (tribe or "").strip().lower(),
            str(int(ark_day)),
            (ark_time or "").strip(),
            (category or "").strip().upper(),
            " ".join((message or "").split()).strip().lower(),
        ]
    )
    return hashlib.sha1(norm.encode("utf-8")).hexdigest()


def hash_api_key(api_key: str) -> str:
    s = (api_key or "").strip()
    return hashlib.sha256(s.encode("utf-8")).hexdigest()


def _csv_to_set(s: str) -> Set[str]:
    parts = [p.strip() for p in (s or "").split(",") if p.strip()]
    return set(parts)


def _set_to_csv(v: Set[str]) -> str:
    parts = [p.strip() for p in (v or set()) if p and str(p).strip()]
    return ",".join(sorted(set(parts)))


@dataclass(frozen=True)
class Tenant:
    id: int
    name: str
    api_key_hash: str
    webhook_url: str
    is_enabled: bool
    log_posting_enabled: bool
    post_delay_seconds: float
    critical_ping_enabled: bool
    critical_ping_role_id: str
    ping_all_critical: bool
    ping_categories: Set[str]


class Db:
    def __init__(self, dsn: str) -> None:
        self._dsn = (dsn or "").strip()
        self._pool: Optional[asyncpg.Pool] = None

    @property
    def pool(self) -> Optional[asyncpg.Pool]:
        return self._pool

    async def start(self) -> None:
        if not self._dsn:
            return

        self._pool = await asyncpg.create_pool(dsn=self._dsn, min_size=1, max_size=5)

        async with self._pool.acquire() as conn:
            # Remove legacy constraints that caused duplicate failures / cross-tenant blocking.
            await conn.execute(_DROP_LEGACY_RAW_LINE_UQ)
            await conn.execute(_DROP_LEGACY_EVENT_HASH_UQ)

            # Create tables
            await conn.execute(_CREATE_TENANTS_TABLE)
            await conn.execute(_CREATE_TRIBE_EVENTS_TABLE)

            # Back-compat: older DBs may not have event_hash or tenant_id yet.
            await conn.execute("ALTER TABLE tribe_events ADD COLUMN IF NOT EXISTS event_hash TEXT;")
            await conn.execute("ALTER TABLE tribe_events ADD COLUMN IF NOT EXISTS tenant_id BIGINT;")

            # Indexes
            await conn.execute(_CREATE_TENANT_EVENT_HASH_UQ)
            await conn.execute(_CREATE_INGESTED_IDX)
            await conn.execute(_CREATE_TENANT_ID_IDX)

    async def close(self) -> None:
        if self._pool is not None:
            await self._pool.close()
            self._pool = None

    async def ensure_legacy_tenant(
        self,
        *,
        legacy_secret: str,
        legacy_tenant_name: str,
        legacy_webhook_url: str,
        legacy_log_posting_enabled: bool,
        legacy_post_delay_seconds: float,
        legacy_critical_ping_enabled: bool,
        legacy_critical_ping_role_id: str,
        legacy_ping_all_critical: bool,
        legacy_ping_categories: List[str],
    ) -> int:
        """
        Ensure a legacy tenant exists for GL_SHARED_SECRET, and backfill existing events to that tenant.
        Returns the tenant id.
        """
        if self._pool is None:
            raise RuntimeError("DB not started")

        key_hash = hash_api_key(legacy_secret)
        cats_csv = ",".join([c.strip() for c in (legacy_ping_categories or []) if c and str(c).strip()])

        sql = """
INSERT INTO tenants (
  name, api_key_hash, webhook_url,
  is_enabled, log_posting_enabled, post_delay_seconds,
  critical_ping_enabled, critical_ping_role_id,
  ping_all_critical, ping_categories
)
VALUES ($1,$2,$3,TRUE,$4,$5,$6,$7,$8,$9)
ON CONFLICT (name) DO UPDATE SET
  api_key_hash = EXCLUDED.api_key_hash,
  webhook_url = EXCLUDED.webhook_url,
  is_enabled = TRUE,
  log_posting_enabled = EXCLUDED.log_posting_enabled,
  post_delay_seconds = EXCLUDED.post_delay_seconds,
  critical_ping_enabled = EXCLUDED.critical_ping_enabled,
  critical_ping_role_id = EXCLUDED.critical_ping_role_id,
  ping_all_critical = EXCLUDED.ping_all_critical,
  ping_categories = EXCLUDED.ping_categories
RETURNING id;
"""
        async with self._pool.acquire() as conn:
            row = await conn.fetchrow(
                sql,
                legacy_tenant_name,
                key_hash,
                (legacy_webhook_url or "").strip(),
                bool(legacy_log_posting_enabled),
                float(legacy_post_delay_seconds or 0.0),
                bool(legacy_critical_ping_enabled),
                (legacy_critical_ping_role_id or "").strip(),
                bool(legacy_ping_all_critical),
                cats_csv,
            )
            tenant_id = int(row["id"])

            # Backfill old rows that predate tenant support.
            await conn.execute("UPDATE tribe_events SET tenant_id=$1 WHERE tenant_id IS NULL;", tenant_id)

        return tenant_id

    async def resolve_tenant_by_key(self, api_key: str) -> Optional[Tenant]:
        if self._pool is None:
            return None
        h = hash_api_key(api_key)
        sql = """
SELECT
  id, name, api_key_hash, webhook_url,
  is_enabled, log_posting_enabled, post_delay_seconds,
  critical_ping_enabled, critical_ping_role_id,
  ping_all_critical, ping_categories
FROM tenants
WHERE api_key_hash = $1
LIMIT 1;
"""
        async with self._pool.acquire() as conn:
            row = await conn.fetchrow(sql, h)
        if row is None:
            return None

        return Tenant(
            id=int(row["id"]),
            name=str(row["name"]),
            api_key_hash=str(row["api_key_hash"]),
            webhook_url=str(row["webhook_url"] or ""),
            is_enabled=bool(row["is_enabled"]),
            log_posting_enabled=bool(row["log_posting_enabled"]),
            post_delay_seconds=float(row["post_delay_seconds"] or 0.0),
            critical_ping_enabled=bool(row["critical_ping_enabled"]),
            critical_ping_role_id=str(row["critical_ping_role_id"] or ""),
            ping_all_critical=bool(row["ping_all_critical"]),
            ping_categories=_csv_to_set(str(row["ping_categories"] or "")),
        )

    async def insert_events(self, events: Iterable[ParsedEvent], *, tenant_id: int) -> List[ParsedEvent]:
        """Insert events and return only newly inserted events (deduped by tenant_id+event_hash)."""
        if self._pool is None:
            return []

        evs = list(events)
        if not evs:
            return []

        cols = "(tenant_id, server, tribe, ark_day, ark_time, severity, category, actor, message, raw_line, event_hash)"

        values_sql = []
        args = []
        for i, e in enumerate(evs):
            base = i * 11
            values_sql.append("(" + ",".join([f"${base + j}" for j in range(1, 12)]) + ")")
            args.extend(
                [
                    int(tenant_id),
                    e.server,
                    e.tribe,
                    int(e.ark_day),
                    e.ark_time,
                    e.severity,
                    e.category,
                    e.actor,
                    e.message,
                    e.raw_line,
                    e.event_hash,
                ]
            )

        sql = (
            f"INSERT INTO tribe_events {cols} VALUES "
            + ",".join(values_sql)
            + " ON CONFLICT DO NOTHING RETURNING event_hash;"
        )

        async with self._pool.acquire() as conn:
            rows = await conn.fetch(sql, *args)
        inserted_hashes = {r["event_hash"] for r in rows if r is not None and r["event_hash"] is not None}

        return [e for e in evs if e.event_hash in inserted_hashes]
