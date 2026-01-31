-- GravityCapture multi-tenant migration (manual option)
--
-- NOTE: The API performs this migration automatically on startup.
-- Run this manually only if you prefer controlling schema changes.

BEGIN;

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

-- Ensure columns exist
ALTER TABLE tribe_events ADD COLUMN IF NOT EXISTS event_hash TEXT;
ALTER TABLE tribe_events ADD COLUMN IF NOT EXISTS tenant_id BIGINT;

-- Drop old single-tenant unique index (if present)
DROP INDEX IF EXISTS tribe_events_event_hash_uq;

-- Optional: drop legacy/raw unique index from earlier revisions
DROP INDEX IF EXISTS tribe_events_raw_line_uidx;

-- Create new tenant-scoped indexes
CREATE UNIQUE INDEX IF NOT EXISTS tribe_events_tenant_event_hash_uq
  ON tribe_events (tenant_id, event_hash)
  WHERE event_hash IS NOT NULL;

CREATE INDEX IF NOT EXISTS tribe_events_tenant_ingested_at_idx
  ON tribe_events (tenant_id, ingested_at DESC);

-- IMPORTANT:
-- 1) Insert at least one tenant row first.
-- 2) Then set tenant_id to a valid tenant id for all existing rows and set a default.
-- Example (replace 1 with your chosen legacy/default tenant id):
--   UPDATE tribe_events SET tenant_id = 1 WHERE tenant_id IS NULL;
--   ALTER TABLE tribe_events ALTER COLUMN tenant_id SET DEFAULT 1;
--   ALTER TABLE tribe_events ALTER COLUMN tenant_id SET NOT NULL;

COMMIT;
