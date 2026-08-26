# Windows deployment

## What the artifact contains

The CI-published `GbexWarehouseAgent-win-x64` artifact is a self-contained
publish output: `GbexWarehouseAgent.exe`, the .NET runtime files it needs
(no separate .NET install required on the warehouse PC), and no
configuration file with any secret in it (there is none — configuration is
entered on first run, see below).

## Installation

1. Copy the entire published folder to the warehouse PC, e.g.
   `C:\Program Files\GbexWarehouseAgent\`.
2. Run `GbexWarehouseAgent.exe` once manually to confirm it starts and the
   first-run configuration flow works (see below) before setting up
   automatic start.
3. No installer is provided in Phase 6 — copy-and-run only. Code signing
   and an MSI/enterprise installer are a documented follow-up (no signing
   certificate is available in this phase).

## First-run configuration

On first launch, open **İstasyon Ayarları** (Station Settings) from the
main window and enter:

- **GBEX API Adresi** — e.g. `https://app.gbex.com.tr`.
- **EasyCube Cihaz Adresi** — the device's local-network URL, e.g.
  `http://192.168.1.50:8080` (or `https://...` if the device's own
  `/websconfig` has `HttpsInUse: true`).
- **Cihaz Kimliği** — optional, the EasyCube's own device identifier if you
  want it recorded explicitly.
- **Tek kullanımlık istasyon anahtarı** — the station secret issued once by
  a GBEX admin (panel.gbex.com.tr → Warehouse Stations). Pasted here and
  saved, it is **never shown again** — it is encrypted with Windows DPAPI
  (scoped to the current Windows user account) and stored at
  `%LOCALAPPDATA%\GbexWarehouseAgent\station.secret`. It is never written to
  any JSON file, the SQLite outbox, or any log.

Use **Bağlantıyı Test Et** (Test Connection) to confirm the entered GBEX API
address and secret actually authenticate before closing the settings
window.

## Automatic start

Not configured by this phase's installer (there isn't one). To start the
Agent automatically at Windows login, add a shortcut to
`GbexWarehouseAgent.exe` in:

```
shell:startup
```

(Win+R → paste that → Enter → drop a shortcut to the .exe there.)

## Uninstall

1. Close the Agent.
2. In Station Settings, click **Kimlik Bilgisini Kaldır** (Remove station
   credential) to delete the DPAPI-encrypted secret file.
3. Delete the application folder.
4. Delete `%LOCALAPPDATA%\GbexWarehouseAgent\` (settings, outbox database,
   logs, any leftover temporary evidence files).
5. Remove the Startup shortcut, if one was added.

## Log location

`%LOCALAPPDATA%\GbexWarehouseAgent\logs\` (structured, rotating — see the
logging configuration in `App.xaml.cs`). Secrets, authorization headers,
customer PII, carrier/label/tracking data, and image bytes are never
written to these logs — only status codes, connection states, retry
counts, and sanitized error codes.

## Simulator mode (no physical hardware)

To exercise the Agent without a real EasyCube device or a real GBEX
backend:

1. Run `Gbex.EasyCube.Simulator` (`dotnet run --project simulator/Gbex.EasyCube.Simulator`)
   — it listens on a local port and reproduces the device's Web API,
   including every required error scenario (see its `POST
   /simulator/configure` endpoint and `ScenarioState.cs`).
2. Point the Agent's **EasyCube Cihaz Adresi** at the simulator's URL
   (printed on startup, e.g. `http://localhost:5xxx`).
3. For the GBEX side, either point at a real GBEX backend with a real test
   station (see the Phase 6 report for the temporary test-station
   procedure), or run the integration test suite
   (`tests/Gbex.Warehouse.Agent.IntegrationTests`), which spins up an
   in-process fake GBEX backend automatically — this is the fastest way to
   exercise the full workflow with zero external dependencies.

## Known limitations only verifiable on the real warehouse PC

See the Phase 6 completion report for the full list (EasyCube timestamp
timezone behavior, the `PackageWeightUnit`/`/measure` auto-send assumptions
in `docs/EASYCUBE_CONTRACT.md`, real USB HID scanner keyboard-wedge
behavior, and DPAPI behavior under the actual warehouse PC's Windows user
account).
