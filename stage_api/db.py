from __future__ import annotations

import hashlib
import secrets
from dataclasses import dataclass
from typing import Iterable, List, Optional, Sequence, Set

import asyncpg

from tribelog.models import ParsedEvent


# -----------------
# Hashing helpers
# -----------------


def _sha256_hex(s: str) -> str:
    return hashlib.sha256((s or "").encode("utf-8")).hexdigest()


def compute_event_hash(
    *,
    server: str,
    tribe: str,
    ark_day: int,
    ark_time: str,
    category: str,
    message: str,
) -> str:
    # Stable and resistant to OCR spacing noise.
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


# -----------------
# Models
# -----------------


@dataclass(frozen=True)
class Tenant:
    id: int
    name: str
    api_key_hash: Optional[str]
    webhook_url: str
    is_enabled: bool

    log_posting_enabled: bool
    post_delay_seconds: float

    critical_ping_enabled: bool
    critical_ping_role_id: str

    ping_all_critical: bool
    ping_categories: Set[str]


# -----------------
# Schema
# -----------------


_CREATE_TENANTS_TABLE = """
CREATE TABLE IF NOT EXISTS tenants (
  id BIGSERIAL PRIMARY KEY,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),

  name TEXT NOT NULL,
  api_key_hash TEXT,

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

_CREATE_TENANTS_KEY_UQ = "CREATE UNIQUE INDEX IF NOT EXISTS tenants_api_key_hash_uq ON tenants(api_key_hash) WHERE api_key_hash IS NOT NULL;"
_CREATE_TENANTS_NAME_UQ = "CREATE UNIQUE INDEX IF NOT EXISTS tenants_name_uq ON tenants(name);"

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
  event_hash TEXT NOT NULL
);
"""

_CREATE_EVENTS_UQ = "CREATE UNIQUE INDEX IF NOT EXISTS tribe_events_tenant_event_hash_uq ON tribe_events(tenant_id, event_hash);"
_CREATE_INGESTED_IDX = "CREATE INDEX IF NOT EXISTS tribe_events_ingested_at_idx ON tribe_events(ingested_at);"
_CREATE_TENANT_IDX = "CREATE INDEX IF NOT EXISTS tribe_events_tenant_id_idx ON tribe_events(tenant_id);"

# Cleanup (older migrations / experiments)
_DROP_LEGACY_EVENT_HASH_UQ = "DROP INDEX IF EXISTS tribe_events_event_hash_uq;"
_DROP_LEGACY_RAW_LINE_UQ = "DROP INDEX IF EXISTS tribe_events_raw_line_uidx;"


# -----------------
# Db
# -----------------


class Db:
    def __init__(self, dsn: str) -> None:
        self._dsn = (dsn or "").strip()
        self._pool: Optional[asyncpg.Pool] = None
        self.default_tenant_id: Optional[int] = None

    @property
    def pool(self) -> Optional[asyncpg.Pool]:
        return self._pool

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
        legacy_ping_categories: Sequence[str],
        tenants_bootstrap_legacy: bool,
    ) -> None:
        if not self._dsn:
            return

        self._pool = await asyncpg.create_pool(dsn=self._dsn, min_size=1, max_size=5)

        async with self._pool.acquire() as conn:
            # Tenants table
            await conn.execute(_CREATE_TENANTS_TABLE)
            await conn.execute(_CREATE_TENANTS_KEY_UQ)
            await conn.execute(_CREATE_TENANTS_NAME_UQ)

            # Events table (create + migrate)
            await conn.execute(_DROP_LEGACY_RAW_LINE_UQ)
            await conn.execute(_DROP_LEGACY_EVENT_HASH_UQ)

            await conn.execute(_CREATE_EVENTS_TABLE)

            # Back-compat: older DBs may not have these columns.
            await conn.execute("ALTER TABLE tribe_events ADD COLUMN IF NOT EXISTS tenant_id BIGINT;")
            await conn.execute("ALTER TABLE tribe_events ADD COLUMN IF NOT EXISTS event_hash TEXT;")
            # Older DBs had event_hash nullable; make it NOT NULL after we ensure it's present for new inserts.
            # (We won't try to backfill event_hash; the app has been writing it for a while.)

            # Ensure indexes exist
            await conn.execute(_CREATE_EVENTS_UQ)
            await conn.execute(_CREATE_INGESTED_IDX)
            await conn.execute(_CREATE_TENANT_IDX)

            # Ensure we have a default tenant, so we can backfill tenant_id on legacy rows.
            self.default_tenant_id = await self._ensure_default_tenant(
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

            # Backfill tenant_id for older rows
            if self.default_tenant_id is not None:
                await conn.execute(
                    "UPDATE tribe_events SET tenant_id = $1 WHERE tenant_id IS NULL;",
                    int(self.default_tenant_id),
                )
                # If the column exists but was nullable, enforce NOT NULL after backfill.
                await conn.execute("ALTER TABLE tribe_events ALTER COLUMN tenant_id SET NOT NULL;")

    async def close(self) -> None:
        if self._pool is not None:
            await self._pool.close()
            self._pool = None

    # -----------------
    # Tenants
    # -----------------

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
        legacy_ping_categories: Sequence[str],
        tenants_bootstrap_legacy: bool,
    ) -> int:
        # If we have a legacy secret and bootstrap is enabled, create a real keyed tenant.
        if tenants_bootstrap_legacy and (legacy_secret or "").strip():
            api_key_hash = _sha256_hex(legacy_secret)
            name = (legacy_tenant_name or "legacy").strip() or "legacy"
            return await self._upsert_tenant(
                conn,
                name=name,
                api_key_hash=api_key_hash,
                webhook_url=legacy_webhook_url,
                log_posting_enabled=legacy_log_posting_enabled,
                post_delay_seconds=legacy_post_delay_seconds,
                critical_ping_enabled=legacy_critical_ping_enabled,
                critical_ping_role_id=legacy_critical_ping_role_id,
                ping_all_critical=legacy_ping_all_critical,
                ping_categories=set(legacy_ping_categories or []),
                is_enabled=True,
            )

        # Otherwise create (or reuse) a keyless default tenant.
        return await self._upsert_tenant(
            conn,
            name=(legacy_tenant_name or "default").strip() or "default",
            api_key_hash=None,
            webhook_url=legacy_webhook_url,
            log_posting_enabled=legacy_log_posting_enabled,
            post_delay_seconds=legacy_post_delay_seconds,
            critical_ping_enabled=legacy_critical_ping_enabled,
            critical_ping_role_id=legacy_critical_ping_role_id,
            ping_all_critical=legacy_ping_all_critical,
            ping_categories=set(legacy_ping_categories or []),
            is_enabled=True,
        )

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
        legacy_ping_categories: Sequence[str],
    ) -> Optional[int]:
        if self._pool is None:
            return None
        if not (legacy_secret or "").strip():
            return None

        api_key_hash = _sha256_hex(legacy_secret)
        name = (legacy_tenant_name or "legacy").strip() or "legacy"

        async with self._pool.acquire() as conn:
            tid = await self._upsert_tenant(
                conn,
                name=name,
                api_key_hash=api_key_hash,
                webhook_url=legacy_webhook_url,
                log_posting_enabled=legacy_log_posting_enabled,
                post_delay_seconds=legacy_post_delay_seconds,
                critical_ping_enabled=legacy_critical_ping_enabled,
                critical_ping_role_id=legacy_critical_ping_role_id,
                ping_all_critical=legacy_ping_all_critical,
                ping_categories=set(legacy_ping_categories or []),
                is_enabled=True,
            )
        self.default_tenant_id = tid
        return tid

    async def _upsert_tenant(
        self,
        conn: asyncpg.Connection,
        *,
        name: str,
        api_key_hash: Optional[str],
        webhook_url: str,
        log_posting_enabled: bool,
        post_delay_seconds: float,
        critical_ping_enabled: bool,
        critical_ping_role_id: str,
        ping_all_critical: bool,
        ping_categories: Set[str],
        is_enabled: bool,
    ) -> int:
        # Use ON CONFLICT by name to keep id stable if name is unchanged.
        row = await conn.fetchrow(
            """
            INSERT INTO tenants (
              name, api_key_hash, webhook_url, is_enabled,
              log_posting_enabled, post_delay_seconds,
              critical_ping_enabled, critical_ping_role_id,
              ping_all_critical, ping_categories
            )
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
            ON CONFLICT (name) DO UPDATE SET
              api_key_hash = EXCLUDED.api_key_hash,
              webhook_url = EXCLUDED.webhook_url,
              is_enabled = EXCLUDED.is_enabled,
              log_posting_enabled = EXCLUDED.log_posting_enabled,
              post_delay_seconds = EXCLUDED.post_delay_seconds,
              critical_ping_enabled = EXCLUDED.critical_ping_enabled,
              critical_ping_role_id = EXCLUDED.critical_ping_role_id,
              ping_all_critical = EXCLUDED.ping_all_critical,
              ping_categories = EXCLUDED.ping_categories
            RETURNING id;
            """,
            (name or "default").strip() or "default",
            api_key_hash,
            (webhook_url or "").strip(),
            bool(is_enabled),
            bool(log_posting_enabled),
            float(post_delay_seconds or 0.0),
            bool(critical_ping_enabled),
            (critical_ping_role_id or "").strip(),
            bool(ping_all_critical),
            ",".join(sorted({c.strip() for c in (ping_categories or set()) if c.strip()})),
        )
        return int(row["id"])  # type: ignore[index]

    async def count_tenants(self) -> int:
        if self._pool is None:
            return 0
        async with self._pool.acquire() as conn:
            val = await conn.fetchval("SELECT COUNT(*) FROM tenants;")
        return int(val or 0)

    async def get_default_tenant(self) -> Optional[Tenant]:
        if self._pool is None or self.default_tenant_id is None:
            return None
        async with self._pool.acquire() as conn:
            row = await conn.fetchrow("SELECT * FROM tenants WHERE id=$1;", int(self.default_tenant_id))
        return self._row_to_tenant(row) if row else None


    async def count_tenants(self) -> int:
        if self._pool is None:
            return 0
        async with self._pool.acquire() as conn:
            row = await conn.fetchrow("SELECT COUNT(*) AS n FROM tenants;")
        return int(row["n"]) if row and row.get("n") is not None else 0

    async def resolve_tenant_by_key(self, api_key: str) -> Optional[Tenant]:
        if self._pool is None:
            return None
        key = (api_key or "").strip()
        if not key:
            return None
        h = _sha256_hex(key)
        async with self._pool.acquire() as conn:
            row = await conn.fetchrow(
                "SELECT * FROM tenants WHERE api_key_hash=$1 AND is_enabled=TRUE;",
                h,
            )
        return self._row_to_tenant(row) if row else None
    async def get_tenant(self, tenant_id: int) -> Optional[Tenant]:
        if self._pool is None:
            return None
        async with self._pool.acquire() as conn:
            row = await conn.fetchrow("SELECT * FROM tenants WHERE id=$1;", int(tenant_id))
        return self._row_to_tenant(row) if row else None

    async def get_default_tenant(self) -> Optional[Tenant]:
        if self.default_tenant_id is None:
            return None
        return await self.get_tenant(int(self.default_tenant_id))



    @staticmethod
    def _row_to_tenant(row: asyncpg.Record) -> Tenant:
        cats = set()
        raw = (row.get("ping_categories") if row else "") or ""
        for p in str(raw).split(","):
            p = p.strip()
            if p:
                cats.add(p)

        return Tenant(
            id=int(row["id"]),
            name=str(row["name"]),
            api_key_hash=row.get("api_key_hash"),
            webhook_url=str(row.get("webhook_url") or ""),
            is_enabled=bool(row.get("is_enabled")),
            log_posting_enabled=bool(row.get("log_posting_enabled")),
            post_delay_seconds=float(row.get("post_delay_seconds") or 0.0),
            critical_ping_enabled=bool(row.get("critical_ping_enabled")),
            critical_ping_role_id=str(row.get("critical_ping_role_id") or ""),
            ping_all_critical=bool(row.get("ping_all_critical")),
            ping_categories=cats,
        )

    # -----------------
    # Events
    # -----------------

    async def insert_events(self, events: Iterable[ParsedEvent], *, tenant_id: int) -> List[ParsedEvent]:
        """Insert events and return only newly inserted events (deduped by (tenant_id,event_hash))."""
        if self._pool is None:
            return []

        evs = list(events)
        if not evs:
            return []

        cols = "(tenant_id, server, tribe, ark_day, ark_time, severity, category, actor, message, raw_line, event_hash)"

        values_sql = []
        args: List[object] = []
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

        sql = f"INSERT INTO tribe_events {cols} VALUES " + ",".join(values_sql) + " ON CONFLICT DO NOTHING RETURNING event_hash;"

        async with self._pool.acquire() as conn:
            rows = await conn.fetch(sql, *args)

        inserted_hashes = {r["event_hash"] for r in rows if r and r.get("event_hash")}
        return [e for e in evs if e.event_hash in inserted_hashes]


def generate_tenant_key() -> str:
    """Generate a URL-safe API key (store only its hash in the DB)."""
    return secrets.token_urlsafe(32)


def hash_tenant_key(key: str) -> str:
    return _sha256_hex(key)
