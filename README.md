# GravityCapture

GravityCapture is a Windows desktop app + web API used to capture **ARK: Survival Ascended** tribe-log screenshots, run OCR/extraction on the server, and post structured results to Discord.

The API supports **multi-tenant** use: each person/server can have their own API key + Discord webhook so multiple users can run the app at the same time without sharing secrets.

---

## What you download

From **Releases**, you’ll typically see one (or both) of these:

- **Installer (.exe)**: easiest for most people.
- **Portable ZIP**: unzip anywhere and run `GravityCapture.exe`.

If you publish as **self-contained**, users do **not** need to install .NET.

---

## Desktop app quick start

1. Download the latest release (installer or portable ZIP).
2. Run `GravityCapture.exe`.
3. Fill in the Settings:
   - **API Base URL**: the API you’re using (example: `https://<your-api>.up.railway.app/`).
   - **Key** (UI may label this as `GL_SHARED_SECRET`):
     - For **new tenants**: your **tenant API key**.
     - For **legacy/single-tenant** setups: the old shared secret still works (if enabled on the API).
   - **Server Name / Tribe Name**: metadata used in messages + stored events.
   - **Capture Interval**: how often to capture.
   - Any other settings you’ve enabled (upscale, timeout, pings, debug).
4. Click **Save**.
5. Click **Start**.

### Where settings are saved

The desktop app stores settings in:

- `%LOCALAPPDATA%\GravityCapture\global.json`

(Deleting this file resets your settings.)

---

## API overview

The API receives screenshots/log-lines from the desktop app, runs OCR/extraction, stores results, and posts to Discord.

Important endpoints:

- `GET /healthz` – health check
- `POST /api/ingest/screenshot` – screenshot ingest
- `POST /api/ingest/log-line` – log-line ingest
- `POST /api/extract` – direct “extract from image” call (used for debugging)

### Auth header

The desktop app sends your key in a request header:

- `X-GL-Key: <your key>`

In multi-tenant mode, the API maps that key to the correct tenant.

---

## Multi-tenant mode (recommended)

### What changes in multi-tenant

- Each tenant gets:
  - a **unique API key**
  - their own **Discord webhook URL**
- Keys are stored as **SHA-256 hashes** in Postgres (the raw key is not recoverable).

### Where tenant info is stored

Tenant configuration is stored in Postgres in a table named:

- `public.tenants`

You can view/edit it with tools like **DBeaver**.

The main event data is stored in:

- `public.tribe_events`

### Rate limiting note (Discord)

Using **one webhook per tenant** is recommended. Discord applies rate limits per webhook and per channel; separate webhooks reduce contention and makes it easier to route to the correct server/channel.

---

## Adding tenants (DBeaver / SQL)

If you already see a `tenants` table in DBeaver, you’re ready.

### Option A (recommended): use the built-in tenant admin script

From your repo root (or wherever `stage_api` lives), run:

```bash
python stage_api/tools/tenant_admin.py --name "Tenant Name" --webhook "https://discord.com/api/webhooks/..."
```

It will print:

- the generated **API key** (save this somewhere safe)
- an `INSERT INTO tenants (...) VALUES (...)` SQL statement

Copy the SQL into DBeaver’s SQL editor and execute it.

Then give the generated API key to that user to paste into the desktop app.

### Option B: manual insert

If you prefer to insert manually, you must store the **SHA-256 hex** of the key in `api_key_hash`.

The script above is safer and avoids mistakes.

### Useful queries

List tenants:

```sql
SELECT id, name, is_enabled, created_at
FROM tenants
ORDER BY id;
```

Disable a tenant:

```sql
UPDATE tenants SET is_enabled = false WHERE id = 2;
```

---

## Deploying the API (Railway)

Minimum environment variables:

- `DATABASE_URL` (Railway provides this)
- `GL_SHARED_SECRET` (used for legacy auth + bootstrap)
- `TENANTS_ENABLED=true` (to require tenant keys)

Notes:

- The API auto-creates required tables on startup.
- In tenant mode, per-tenant webhook URLs live in the database; you do not need a single global webhook (unless you’re using legacy mode).

---

## Building / publishing releases

### Visual Studio (manual)

- Configuration: `Release`
- Target framework: `net8.0-windows`
- Target runtime: `win-x64`
- Deployment mode: `Self-contained`

Optional:

- **Produce single file** (nice for distribution)

Publish output can be zipped and uploaded to GitHub Releases.

### GitHub Actions (tag-based)

This repo includes a workflow that builds on tags. Create and push a tag like:

- `v1.0.0`
- `v1.0.0-alpha`

Then GitHub Actions will build and attach artifacts to the release.

---

## Troubleshooting

**API shows 502 Bad Gateway (Railway)**

- Check Railway logs for startup errors.
- Confirm `DATABASE_URL` exists and points to the right Postgres.
- Confirm the app is listening on the port Railway provides.

**Desktop app won’t post / unauthorized**

- Verify the API Base URL is correct.
- Verify the key in the desktop app matches the tenant key you created.
- Confirm the tenant is `is_enabled = true`.

