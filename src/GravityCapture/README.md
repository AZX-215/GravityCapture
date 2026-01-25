# GravityCapture (Desktop)

This folder contains a Windows WPF desktop app that:
- Captures a calibrated (drag-to-select) region of the screen (stored as normalized coordinates so it scales with resolution)
- Shows a large live preview of the cropped capture
- Sends the cropped PNG to the stage API: `POST /ingest/screenshot`
- Optional debug call: `POST /extract`

## Build

If you see duplicate-type build errors (e.g., AppSettings/ApiClient defined twice), you likely extracted a new patch on top of older files that were not overwritten. Fix: delete the entire `src/GravityCapture` folder and replace it from the zip, or run `CLEANUP_IF_BUILD_ERRORS.ps1` then rebuild.

- Requires Visual Studio 2022 or `dotnet` SDK 8.x (Windows)
- Open `GravityCapture.sln` and Run

## Settings
Settings are saved to:
`%AppData%\GravityCapture\settings.json`

Key options:
- API Base URL
- GL_SHARED_SECRET (sent as `X-GL-Key`)
- Server / Tribe names
- Capture interval
- CRITICAL ping toggle (sent as `critical_ping=1|0`)

## Calibrate
Open ARK, bring up the Tribe Log, then click **Calibrate Region** and drag a rectangle around the log area.
