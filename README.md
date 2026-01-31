Gravity Capture is a log-bot for ARK: Survival Ascended.

This repo contains:

- `stage_api/` (FastAPI): OCRs tribe-log screenshots, de-dupes in Postgres, posts events to Discord, and optionally pings a role for selected CRITICAL categories.
- `src/GravityCapture/GravityCapture/` (Windows app): captures a fixed on-screen region of the tribe log and sends it to the API.

## Railway deployment (stage_api)

The API is deployed using a **Dockerfile** (`stage_api/Dockerfile`). This installs the system libraries needed by OpenCV (fixes `libxcb.so.1` crashes on Railway) plus Tesseract.

Required environment variables on the **screenshots-api-stage** service:

- `DATABASE_URL` (Postgres connection string)
- `GL_SHARED_SECRET` (shared key; the desktop app sends it in `X-GL-Key` / `x-api-key`)
- `ALERT_DISCORD_WEBHOOK_URL` (Discord webhook)

Optional:

- `OCR_ENGINE` = `auto` (default) | `ppocr` | `tesseract`
- `LOG_POSTING_ENABLED` = `true` (default) | `false`
- `POST_DELAY_SECONDS` = `0.8` (default)
- `CRITICAL_PING_ENABLED` = `true` (default) | `false`
- `CRITICAL_PING_ROLE_ID` = `#`
- `PING_ALL_CRITICAL` = `false` (default)
- `PING_CATEGORIES` = `STRUCTURE_DESTROYED,TRIBE_KILLED_PLAYER` (default)
- `ENVIRONMENT` = `stage` | `main`
