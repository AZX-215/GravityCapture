from __future__ import annotations

import argparse
import hashlib
import secrets


def _sha256_hex(s: str) -> str:
    return hashlib.sha256((s or "").encode("utf-8")).hexdigest()


def main() -> int:
    p = argparse.ArgumentParser(description="Generate a tenant API key + SQL to upsert into the tenants table.")
    p.add_argument("--name", required=True, help="Tenant name (e.g. 'Gravity - Server A')")
    p.add_argument("--webhook", required=True, help="Discord webhook URL for this tenant")
    p.add_argument("--post-delay", type=float, default=0.8, help="Seconds between Discord posts (default: 0.8)")
    p.add_argument("--log-posting-enabled", action="store_true", default=True)
    p.add_argument("--log-posting-disabled", action="store_false", dest="log_posting_enabled")

    p.add_argument("--critical-ping-enabled", action="store_true", default=True)
    p.add_argument("--critical-ping-disabled", action="store_false", dest="critical_ping_enabled")
    p.add_argument("--critical-role-id", default="", help="Discord role ID to mention when pinging (optional)")
    p.add_argument("--ping-all-critical", action="store_true", default=False)
    p.add_argument(
        "--ping-categories",
        default="STRUCTURE_DESTROYED,TRIBE_KILLED_PLAYER",
        help="Comma-separated categories to ping when severity=CRITICAL (only if --ping-all-critical is not set)",
    )

    args = p.parse_args()

    tenant_key = secrets.token_urlsafe(32)
    key_hash = _sha256_hex(tenant_key)
    cats = ",".join([c.strip() for c in (args.ping_categories or "").split(",") if c.strip()])

    print("TENANT_NAME=" + args.name)
    print("TENANT_KEY=" + tenant_key)
    print()
    print("-- SQL (run against Railway Postgres) --")
    print(
        """
INSERT INTO tenants (
  name, api_key_hash, webhook_url, is_enabled,
  log_posting_enabled, post_delay_seconds,
  critical_ping_enabled, critical_ping_role_id,
  ping_all_critical, ping_categories
)
VALUES (
  '{name}', '{hash}', '{webhook}', TRUE,
  {log_posting_enabled}, {post_delay},
  {critical_ping_enabled}, '{role_id}',
  {ping_all_critical}, '{ping_categories}'
)
ON CONFLICT (name) DO UPDATE SET
  api_key_hash = EXCLUDED.api_key_hash,
  webhook_url = EXCLUDED.webhook_url,
  is_enabled = EXCLUDED.is_enabled,
  log_posting_enabled = EXCLUDED.log_posting_enabled,
  post_delay_seconds = EXCLUDED.post_delay_seconds,
  critical_ping_enabled = EXCLUDED.critical_ping_enabled,
  critical_ping_role_id = EXCLUDED.critical_ping_role_id,
  ping_all_critical = EXCLUDED.ping_all_critical,
  ping_categories = EXCLUDED.ping_categories;
""".format(
            name=args.name.replace("'", "''"),
            hash=key_hash,
            webhook=args.webhook.replace("'", "''"),
            log_posting_enabled="TRUE" if args.log_posting_enabled else "FALSE",
            post_delay=float(args.post_delay),
            critical_ping_enabled="TRUE" if args.critical_ping_enabled else "FALSE",
            role_id=(args.critical_role_id or "").replace("'", "''"),
            ping_all_critical="TRUE" if args.ping_all_critical else "FALSE",
            ping_categories=cats.replace("'", "''"),
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
