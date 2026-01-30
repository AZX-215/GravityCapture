from __future__ import annotations

import hashlib
from dataclasses import dataclass
from typing import Iterable, List, Optional

import asyncpg

from tribelog.models import ParsedEvent


_CREATE_TABLE = """
CREATE TABLE IF NOT EXISTS tribe_events (
  id BIGSERIAL PRIMARY KEY,
  ingested_at TIMESTAMPTZ NOT NULL DEFAULT now(),
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
_CREATE_UNIQUE = "CREATE UNIQUE INDEX IF NOT EXISTS tribe_events_event_hash_uq ON tribe_events (event_hash);"
_CREATE_INGESTED_IDX = "CREATE INDEX IF NOT EXISTS tribe_events_ingested_at_idx ON tribe_events (ingested_at);"


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


class Db:
    def __init__(self, dsn: str) -> None:
        self._dsn = (dsn or "").strip()
        self._pool: Optional[asyncpg.Pool] = None

    async def start(self) -> None:
        if not self._dsn:
            return
        self._pool = await asyncpg.create_pool(dsn=self._dsn, min_size=1, max_size=5)
        async with self._pool.acquire() as conn:
            # Cleanup legacy unique index that caused duplicate raw_line failures
            await conn.execute("DROP INDEX IF EXISTS tribe_events_raw_line_uidx;")
            await conn.execute(_CREATE_TABLE)

            # Back-compat: older DBs may not have event_hash yet.
            await conn.execute("ALTER TABLE tribe_events ADD COLUMN IF NOT EXISTS event_hash TEXT;")

            # Ensure index exists
            await conn.execute(_CREATE_UNIQUE)
            await conn.execute(_CREATE_INGESTED_IDX)

    async def close(self) -> None:
        if self._pool is not None:
            await self._pool.close()
            self._pool = None

    async def insert_events(self, events: Iterable[ParsedEvent]) -> List[ParsedEvent]:
        """Insert events and return only newly inserted events (deduped by event_hash)."""
        if self._pool is None:
            return []

        evs = list(events)
        if not evs:
            return []

        cols = "(server, tribe, ark_day, ark_time, severity, category, actor, message, raw_line, event_hash)"
        # Build a VALUES list with positional args.
        values_sql = []
        args = []
        for i, e in enumerate(evs):
            base = i * 10
            values_sql.append(
                "("
                + ",".join([f"${base + j}" for j in range(1, 11)])
                + ")"
            )
            args.extend(
                [
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
