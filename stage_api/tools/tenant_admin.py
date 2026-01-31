#!/usr/bin/env python3
"""Utility to generate tenant keys and SQL inserts.

This DOES NOT talk to your database. It prints safe values you can copy into
Railway Postgres (Query tab / psql).

Examples:
  python stage_api/tools/tenant_admin.py --name "Anthony" --webhook "https://discord.com/api/webhooks/..." \
    --role-id "1286835166471262249" --ping-categories "STRUCTURE_DESTROYED,TRIBE_KILLED_PLAYER"

If you omit --key, one is generated.
"""

from __future__ import annotations

import argparse
import hashlib
import secrets
import sys


def sha256_hex(s: str) -> str:
    return hashlib.sha256(s.encode("utf-8")).hexdigest()


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--name", required=True, help="Tenant name (e.g., user or server name)")
    p.add_argument("--webhook", required=True, help="Discord webhook URL for this tenant")
    p.add_argument("--key", default="", help="API key to give the tenant (leave blank to generate)")
    p.add_argument("--enabled", default="true", help="true/false")
    p.add_argument("--log-posting-enabled", default="true", help="true/false")
    p.add_argument("--post-delay", default="0.8", help="Seconds between webhook posts")
    p.add_argument("--critical-ping-enabled", default="true", help="true/false")
    p.add_argument("--role-id", default="", help="Discord role id for @role pings")
    p.add_argument("--ping-all-critical", default="false", help="true/false")
    p.add_argument(
        "--ping-categories",
        default="STRUCTURE_DESTROYED,TRIBE_KILLED_PLAYER",
        help="Comma-separated categories that trigger pings when severity is CRITICAL",
    )

    args = p.parse_args()

    key = (args.key or "").strip()
    if not key:
        # 32 url-safe characters ~= 192 bits
        key = secrets.token_urlsafe(24)

    key_hash = sha256_hex(key)

    def b(s: str) -> str:
        return "true" if s.strip().lower() in {"1", "true", "yes", "y", "on"} else "false"

    enabled = b(args.enabled)
    log_posting_enabled = b(args.log_posting_enabled)
    critical_ping_enabled = b(args.critical_ping_enabled)
    ping_all_critical = b(args.ping_all_critical)

    try:
        post_delay = float(args.post_delay)
    except ValueError:
        print("Invalid --post-delay; must be a number", file=sys.stderr)
        return 2

    # NOTE: We store ping_categories as a CSV string in DB.
    ping_categories = ",".join([p.strip() for p in (args.ping_categories or "").split(",") if p.strip()])

    print("\n=== Tenant API Key (give this to the user / put in Desktop App) ===\n")
    print(key)
    print("\n=== Key hash stored in DB ===\n")
    print(key_hash)

    print("\n=== SQL (run in Railway Postgres) ===\n")
    # Use dollar quoting for the webhook url in case it contains special chars.
    sql = f"""
INSERT INTO tenants (
  name,
  key_hash,
  webhook_url,
  is_enabled,
  log_posting_enabled,
  post_delay_seconds,
  critical_ping_enabled,
  critical_ping_role_id,
  ping_all_critical,
  ping_categories
) VALUES (
  {args.name!r},
  {key_hash!r},
  {args.webhook!r},
  {enabled},
  {log_posting_enabled},
  {post_delay},
  {critical_ping_enabled},
  {args.role_id!r},
  {ping_all_critical},
  {ping_categories!r}
)
ON CONFLICT (key_hash) DO UPDATE SET
  name = EXCLUDED.name,
  webhook_url = EXCLUDED.webhook_url,
  is_enabled = EXCLUDED.is_enabled,
  log_posting_enabled = EXCLUDED.log_posting_enabled,
  post_delay_seconds = EXCLUDED.post_delay_seconds,
  critical_ping_enabled = EXCLUDED.critical_ping_enabled,
  critical_ping_role_id = EXCLUDED.critical_ping_role_id,
  ping_all_critical = EXCLUDED.ping_all_critical,
  ping_categories = EXCLUDED.ping_categories;
"""
    print(sql.strip())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
