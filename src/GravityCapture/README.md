# GravityCapture (Desktop)

This folder contains a Windows WPF desktop app that:
- Captures a calibrated (drag-to-select) region of the screen (stored as normalized coordinates so it scales with resolution)
- Shows a large live preview of the cropped capture
- Sends the cropped PNG to the stage API: `POST /ingest/screenshot`
- Optional debug call: `POST /extract`

## Build
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
