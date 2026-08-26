# GBEX Warehouse Agent

A Windows-native hardware bridge between a GBEX depot's EasyCube dimensioner
and the GBEX backend (gbex.com.tr). **This is a separate repository from the
gbex.com.tr website** — no shared history, dependencies, CI, configuration,
or deployment artifacts.

## What this is (and isn't)

The Agent is **only a secure hardware bridge**. It authenticates as a
machine (station Bearer token, never a human session), reads barcode scans,
talks to the EasyCube dimensioner, and reports raw measurement facts to
GBEX. It contains **zero** wallet, pricing, carrier-selection,
customer-approval, or shipment-replacement logic, and never holds a Karrio
or database credential — the GBEX backend remains the sole source of truth
and decision-maker for everything financial or carrier-related. See
`tests/Gbex.Warehouse.Agent.Core.Tests/ScopeBoundaryTests.cs` for the
structural tests that enforce this.

`warehouseReplacementEnabled` on the GBEX backend stays `false` regardless
of anything in this repository — this Agent cannot enable it and never
tries to.

## Project structure

```
gbex-warehouse-agent/
├── src/
│   ├── Gbex.Warehouse.Agent.Core/            # workflow engine, no HTTP/SQLite/WPF
│   ├── Gbex.Warehouse.Agent.Infrastructure/  # HTTP clients, SQLite outbox, heartbeat
│   └── Gbex.Warehouse.Agent.Windows/         # WPF shell, DPAPI secret store
├── tests/
│   ├── Gbex.Warehouse.Agent.Core.Tests/
│   └── Gbex.Warehouse.Agent.IntegrationTests/
├── simulator/
│   └── Gbex.EasyCube.Simulator/              # reproduces the real device's Web API
├── docs/
└── .github/workflows/ci.yml                  # Windows runner: build, test, publish
```

## Workflow

```
IDLE → scan GBEX barcode → look up order via GBEX API →
display declared weight/dimensions → trigger EasyCube capture →
correlate barcode + device + package number → submit raw facts to GBEX →
PASS (delete temp image, return to ready) or
MISMATCH (order → on_hold via the backend, upload evidence, delete local
image, display "ON HOLD — OPERATOR RESOLUTION REQUIRED")
```

No carrier label is purchased or printed in this phase.

## Building

Requires the .NET 8 SDK. Core, Infrastructure, the simulator, and both test
projects build and run on any OS. The Windows/WPF project requires an
actual Windows machine (or the CI Windows runner) — WPF cannot cross-compile
from macOS/Linux.

```bash
dotnet restore Gbex.Warehouse.Agent.sln
dotnet build Gbex.Warehouse.Agent.sln
dotnet test tests/Gbex.Warehouse.Agent.Core.Tests
dotnet test tests/Gbex.Warehouse.Agent.IntegrationTests
```

See `docs/DEPLOYMENT.md` for installing the published Windows artifact and
running against the EasyCube simulator with no physical hardware, and
`docs/GBEX_API_CONTRACT.md` / `docs/EASYCUBE_CONTRACT.md` for the exact
wire contracts this Agent implements.
