from __future__ import annotations

import argparse
import hashlib
import secrets
import sys
from typing import List


def _hash_key(key: str) -> str:
    return hashlib.sha256(key.strip().encode("utf-8")).hexdigest()


def _csv_to_list(s: str) -> List[str]:
    return [p.strip() for p in (s or "").split(",") if p.strip()]


def main(argv: List[str]) -> int:
    p = argparse.ArgumentParser(description="Generate a tenant API key and SQL to add/update a tenant row.")
    p.add_argument("--name", required=True, help="Tenant name (unique).")
    p.add_argument("--webhook", required=True, help="Discord webhook URL for this tenant.")
    p.add_argument("--post-delay", type=float, default=0.8, help="Delay between posts (seconds).")
    p.add_argument("--log-posting-enabled", type=int, default=1, help="1 to enable posting, 0 to disable.")
    p.add_argument("--critical-ping-enabled", type=int, default=1, help="1 to allow critical pings, 0 to disable.")
    p.add_argument("--critical-role-id", default="", help="Discord role id to mention for pings.")
    p.add_argument("--ping-all-critical", type=int, default=0, help="1 to ping for all CRITICAL, 0 to restrict by category.")
    p.add_argument(
        "--ping-categories",
        default="STRUCTURE_DESTROYED,TRIBE_KILLED_PLAYER",
        help="Comma-separated CRITICAL categories to ping when --ping-all-critical=0",
    )
    p.add_argument("--reuse-key", default="", help="Provide an existing key (otherwise one will be generated).")
    args = p.parse_args(argv)

    key = args.reuse_key.strip() if args.reuse_key else secrets.token_urlsafe(32)
    key_hash = _hash_key(key)
    cats = ",".join(_csv_to_list(args.ping_categories))

    # NOTE: api_key_hash is UNIQUE; name is UNIQUE. We upsert by name and keep the api_key_hash stable unless you supply --reuse-key.
    sql = f"""-- Run this in Railway Postgres (Data tab -> Query) for your DB:
INSERT INTO tenants (
  name, api_key_hash, webhook_url,
  is_enabled, log_posting_enabled, post_delay_seconds,
  critical_ping_enabled, critical_ping_role_id,
  ping_all_critical, ping_categories
)
VALUES (
  '{args.name.replace("'", "''")}',
  '{key_hash}',
  '{args.webhook.replace("'", "''")}',
  TRUE,
  {1 if args.log_posting_enabled else 0},
  {float(args.post_delay)},
  {1 if args.critical_ping_enabled else 0},
  '{str(args.critical_role_id).replace("'", "''")}',
  {1 if args.ping_all_critical else 0},
  '{cats.replace("'", "''")}'
)
ON CONFLICT (name) DO UPDATE SET
  webhook_url = EXCLUDED.webhook_url,
  is_enabled = TRUE,
  log_posting_enabled = EXCLUDED.log_posting_enabled,
  post_delay_seconds = EXCLUDED.post_delay_seconds,
  critical_ping_enabled = EXCLUDED.critical_ping_enabled,
  critical_ping_role_id = EXCLUDED.critical_ping_role_id,
  ping_all_critical = EXCLUDED.ping_all_critical,
  ping_categories = EXCLUDED.ping_categories;
"""

    print("Tenant API key (copy into the desktop app X-GL-Key):")
    print(key)
    print()
    print(sql)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
