# Backend changes that would improve the Agent (none are blocking)

Per the Phase 6 brief: this repo never modifies the gbex.com.tr website
repo. Anything here is a request for a FUTURE backend change, tracked
separately — the Agent works correctly today without any of these.

## 1. Distinguish "revoked token" from "disabled station" on 401

`lib/auth/warehouse.ts`'s `getStationIdentity` returns `null` for BOTH an
unknown/invalid token AND a disabled station's valid token
(`!station.enabled` check), and every warehouse route returns the same
`401 { message: "Yetkisiz erişim." }` either way.

The Agent's `IGbexApiClient` already defines a distinct
`GbexApiResult.StationDisabled` case (and `HeartbeatService`/the WPF UI
already render a distinct "disabled" state) for exactly this scenario, but
nothing in the current backend response can trigger it — `GbexApiClient`
currently maps every 401/403 to `Unauthorized`.

**Suggested backend change**: have `getStationIdentity` return a
discriminated result (e.g. `{ kind: "not_found" | "disabled", ... }`
instead of a bare `null`), and have each warehouse route return a distinct
status/body for the disabled case — e.g. `403` with
`{ message: "İstasyon devre dışı.", code: "station_disabled" }` vs the
existing `401` for a genuinely invalid/unknown token. `GbexApiClient` would
then map `code: "station_disabled"` to `GbexApiResult.StationDisabled`
with a one-line change.

No other change is requested or required for Phase 6.
