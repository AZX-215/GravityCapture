from __future__ import annotations

import hashlib
from dataclasses import dataclass
from typing import Iterable, List, Optional, Set

import asyncpg

from tribelog.models import ParsedEvent


# -----------------------------
# Helpers
# -----------------------------

def _hash_key(key: str) -> str:
    """SHA-256 hash of an API key for storage/lookup."""
    k = (key or "").strip()
    return hashlib.sha256(k.encode("utf-8")).hexdigest()


def _csv_to_set(csv: str) -> Set[str]:
    parts = [p.strip() for p in (csv or "").split(",") if p.strip()]
    return {p.upper() for p in parts}


def _set_to_csv(vals: Set[str]) -> str:
    return ",".join(sorted({(v or "").strip().upper() for v in (vals or set()) if (v or "").strip()}))


# -----------------------------
# Models
# -----------------------------

@dataclass(frozen=True)
class Tenant:
    id: int
    name: str
    key_hash: str
    webhook_url: str
    log_posting_enabled: bool
    post_delay_seconds: float
    critical_ping_enabled: bool
    critical_ping_role_id: str
    ping_all_critical: bool
    ping_categories: Set[str]
    is_enabled: bool


# -----------------------------
# Schema
# -----------------------------

_CREATE_TENANTS_TABLE = """
CREATE TABLE IF NOT EXISTS tenants (
  id BIGSERIAL PRIMARY KEY,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  name TEXT NOT NULL,
  key_hash TEXT NOT NULL UNIQUE,
  webhook_url TEXT NOT NULL,
  log_posting_enabled BOOLEAN NOT NULL DEFAULT TRUE,
  post_delay_seconds DOUBLE PRECISION NOT NULL DEFAULT 0.8,
  critical_ping_enabled BOOLEAN NOT NULL DEFAULT TRUE,
  critical_ping_role_id TEXT NOT NULL DEFAULT '',
  ping_all_critical BOOLEAN NOT NULL DEFAULT FALSE,
  ping_categories TEXT NOT NULL DEFAULT '',
  is_enabled BOOLEAN NOT NULL DEFAULT TRUE
);
"""

_CREATE_EVENTS_TABLE = """
CREATE TABLE IF NOT EXISTS tribe_events (
  id BIGSERIAL PRIMARY KEY,
  ingested_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  tenant_id BIGINT NOT NULL,
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

# Indexes/constraints
_CREATE_UNIQUE_TENANT_HASH = (
    "CREATE UNIQUE INDEX IF NOT EXISTS tribe_events_tenant_event_hash_uq "
    "ON tribe_events (tenant_id, event_hash) WHERE event_hash IS NOT NULL;"
)
_CREATE_TENANT_INGESTED_IDX = (
    "CREATE INDEX IF NOT EXISTS tribe_events_tenant_ingested_at_idx "
    "ON tribe_events (tenant_id, ingested_at DESC);"
)


def compute_event_hash(
    *,
    server: str,
    tribe: str,
    ark_day: int,
    ark_time: str,
    category: str,
    message: str,
) -> str:
    """Stable hash for dedupe.

    Note: tenant scoping is handled at the DB layer via (tenant_id, event_hash) uniqueness.
    """
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


class Db:
    def __init__(self, dsn: str) -> None:
        self._dsn = (dsn or "").strip()
        self._pool: Optional[asyncpg.Pool] = None
        self._default_tenant_id: Optional[int] = None

    @property
    def is_ready(self) -> bool:
        return self._pool is not None

    @property
    def default_tenant_id(self) -> Optional[int]:
        return self._default_tenant_id

    async def start(
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
        legacy_ping_categories: Set[str],
        tenants_bootstrap_legacy: bool,
    ) -> None:
        if not self._dsn:
            return

        self._pool = await asyncpg.create_pool(dsn=self._dsn, min_size=1, max_size=5)

        async with self._pool.acquire() as conn:
            # 1) tenants
            await conn.execute(_CREATE_TENANTS_TABLE)

            # 2) events table + back-compat columns
            #    (Create with tenant_id; older DBs will be altered below.)
            await conn.execute(_CREATE_EVENTS_TABLE)

            # Drop old index that previously caused duplicate conflicts
            await conn.execute("DROP INDEX IF EXISTS tribe_events_raw_line_uidx;")

            # Ensure tenant_id + event_hash exist for older DBs
            await conn.execute("ALTER TABLE tribe_events ADD COLUMN IF NOT EXISTS tenant_id BIGINT;")
            await conn.execute("ALTER TABLE tribe_events ADD COLUMN IF NOT EXISTS event_hash TEXT;")

            # Remove the old global unique index if present (tenant-scoped index replaces it).
            await conn.execute("DROP INDEX IF EXISTS tribe_events_event_hash_uq;")

            # 3) Ensure default/legacy tenant exists (bootstrap)
            default_tenant_id = await self._ensure_default_tenant(
                conn,
                legacy_secret=legacy_secret,
                legacy_tenant_name=legacy_tenant_name,
                legacy_webhook_url=legacy_webhook_url,
                legacy_log_posting_enabled=legacy_log_posting_enabled,
                legacy_post_delay_seconds=legacy_post_delay_seconds,
                legacy_critical_ping_enabled=legacy_critical_ping_enabled,
                legacy_critical_ping_role_id=legacy_critical_ping_role_id,
                legacy_ping_all_critical=legacy_ping_all_critical,
                legacy_ping_categories=legacy_ping_categories,
                tenants_bootstrap_legacy=tenants_bootstrap_legacy,
            )
            self._default_tenant_id = default_tenant_id

            # Backfill existing rows
            if default_tenant_id is not None:
                await conn.execute(
                    "UPDATE tribe_events SET tenant_id=$1 WHERE tenant_id IS NULL;",
                    int(default_tenant_id),
                )
                # Ensure tenant_id is enforced going forward
                await conn.execute("ALTER TABLE tribe_events ALTER COLUMN tenant_id SET NOT NULL;")
                await conn.execute(f"ALTER TABLE tribe_events ALTER COLUMN tenant_id SET DEFAULT {int(default_tenant_id)};")

            # 4) Create new indexes
            await conn.execute(_CREATE_UNIQUE_TENANT_HASH)
            await conn.execute(_CREATE_TENANT_INGESTED_IDX)

    async def close(self) -> None:
        if self._pool is not None:
            await self._pool.close()
            self._pool = None

    async def _ensure_default_tenant(
        self,
        conn: asyncpg.Connection,
        *,
        legacy_secret: str,
        legacy_tenant_name: str,
        legacy_webhook_url: str,
        legacy_log_posting_enabled: bool,
        legacy_post_delay_seconds: float,
        legacy_critical_ping_enabled: bool,
        legacy_critical_ping_role_id: str,
        legacy_ping_all_critical: bool,
        legacy_ping_categories: Set[str],
        tenants_bootstrap_legacy: bool,
    ) -> Optional[int]:
        """Create/update the legacy/default tenant.

        This supports "automatic legacy tenant bootstrap": existing deployments using GL_SHARED_SECRET
        and ALERT_DISCORD_WEBHOOK_URL keep working after enabling tenant mode.
        """
        if not tenants_bootstrap_legacy:
            return None

        # If no legacy_secret is provided, create a non-authenticated default tenant keyed off a constant.
        # This still allows operator-created tenants to work; the default tenant is mainly for backfill.
        secret_for_hash = (legacy_secret or "__legacy_default__").strip()
        key_hash = _hash_key(secret_for_hash)

        # Upsert by key_hash
        row = await conn.fetchrow(
            "SELECT id FROM tenants WHERE key_hash=$1;",
            key_hash,
        )

        ping_categories_csv = _set_to_csv(legacy_ping_categories)

        if row and row.get("id") is not None:
            tenant_id = int(row["id"])
            await conn.execute(
                """
                UPDATE tenants
                SET name=$2,
                    webhook_url=$3,
                    log_posting_enabled=$4,
                    post_delay_seconds=$5,
                    critical_ping_enabled=$6,
                    critical_ping_role_id=$7,
                    ping_all_critical=$8,
                    ping_categories=$9,
                    is_enabled=TRUE
                WHERE id=$1;
                """,
                tenant_id,
                (legacy_tenant_name or "Legacy"),
                (legacy_webhook_url or ""),
                bool(legacy_log_posting_enabled),
                float(legacy_post_delay_seconds or 0.0),
                bool(legacy_critical_ping_enabled),
                (legacy_critical_ping_role_id or "").strip(),
                bool(legacy_ping_all_critical),
                ping_categories_csv,
            )
            return tenant_id

        # Insert new default tenant
        inserted = await conn.fetchrow(
            """
            INSERT INTO tenants (
              name,
              key_hash,
              webhook_url,
              log_posting_enabled,
              post_delay_seconds,
              critical_ping_enabled,
              critical_ping_role_id,
              ping_all_critical,
              ping_categories,
              is_enabled
            ) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,TRUE)
            RETURNING id;
            """,
            (legacy_tenant_name or "Legacy"),
            key_hash,
            (legacy_webhook_url or ""),
            bool(legacy_log_posting_enabled),
            float(legacy_post_delay_seconds or 0.0),
            bool(legacy_critical_ping_enabled),
            (legacy_critical_ping_role_id or "").strip(),
            bool(legacy_ping_all_critical),
            ping_categories_csv,
        )
        if inserted and inserted.get("id") is not None:
            return int(inserted["id"])
        return None

    async def resolve_tenant_by_key(self, key: str) -> Optional[Tenant]:
        """Resolve a tenant record from a presented key (X-GL-Key / x-api-key)."""
        if self._pool is None:
            return None
        k = (key or "").strip()
        if not k:
            return None

        key_hash = _hash_key(k)

        async with self._pool.acquire() as conn:
            row = await conn.fetchrow(
                """
                SELECT
                  id,
                  name,
                  key_hash,
                  webhook_url,
                  log_posting_enabled,
                  post_delay_seconds,
                  critical_ping_enabled,
                  critical_ping_role_id,
                  ping_all_critical,
                  ping_categories,
                  is_enabled
                FROM tenants
                WHERE key_hash=$1;
                """,
                key_hash,
            )

        if not row:
            return None

        return Tenant(
            id=int(row["id"]),
            name=str(row["name"]),
            key_hash=str(row["key_hash"]),
            webhook_url=str(row["webhook_url"]),
            log_posting_enabled=bool(row["log_posting_enabled"]),
            post_delay_seconds=float(row["post_delay_seconds"] or 0.0),
            critical_ping_enabled=bool(row["critical_ping_enabled"]),
            critical_ping_role_id=str(row["critical_ping_role_id"] or "").strip(),
            ping_all_critical=bool(row["ping_all_critical"]),
            ping_categories=_csv_to_set(str(row["ping_categories"] or "")),
            is_enabled=bool(row["is_enabled"]),
        )

    async def insert_events(self, events: Iterable[ParsedEvent], *, tenant_id: int) -> List[ParsedEvent]:
        """Insert events and return only newly inserted events (deduped by (tenant_id, event_hash))."""
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

        inserted_hashes = {r["event_hash"] for r in rows if r and r.get("event_hash")}
        return [e for e in evs if e.event_hash in inserted_hashes]

    async def count_tenants(self) -> int:
        if self._pool is None:
            return 0
        async with self._pool.acquire() as conn:
            row = await conn.fetchrow("SELECT COUNT(*) AS c FROM tenants;")
        if not row:
            return 0
        return int(row["c"])
