# GBEX warehouse API contract (as read from the gbex.com.tr website repo)

This is a read-only transcription of the exact routes the Agent calls. The
gbex website repo is the source of truth; this document is NOT authoritative
— if the two ever disagree, the website repo wins and this file needs
updating (the Agent's own repo cannot make backend changes; see
`BACKEND_CHANGES_NEEDED.md` for anything that would require one).

## Authentication

`Authorization: Bearer <station-secret>` — a machine credential, NOT a human
session cookie. Distinct scopes: `warehouse:heartbeat`, `warehouse:scan`,
`warehouse:measure` (evidence upload also requires `warehouse:measure`).

A missing/invalid/unknown/disabled-station token all currently return the
same `401 { message }` — see `BACKEND_CHANGES_NEEDED.md`.

## `POST /api/warehouse/heartbeat`

Body: `{ agentVersion?: string }` (optional).
Response: `200 { ok: true, station: string }`.

## `POST /api/warehouse/orders/lookup`

Body: `{ barcode: string }` — must match `^GBEX\d{10}$`.
Response: `200 { order: StationOrderDTO }` or `404 { message }` /
`422 { message }` for an invalid barcode.

`StationOrderDTO` (exact fields, nothing else — see
`lib/dto/warehouse-order.ts`):

```json
{
  "id": "string",
  "gbexBarcode": "string",
  "status": "string",
  "destinationCountry": "string",
  "destinationCity": "string",
  "declaredWeight": 0,
  "declaredDesi": 0,
  "declaredLength": 0,
  "declaredWidth": 0,
  "declaredHeight": 0
}
```

No carrier identity, no price/currency, no customer PII — this is
deliberately narrower than the human ops/admin view.

## `POST /api/warehouse/measurements`

Headers: `Idempotency-Key: <string>` (required).
Body — RAW HARDWARE FACTS ONLY:

```json
{
  "barcode": "string",
  "weightKg": 0,
  "lengthCm": 0,
  "widthCm": 0,
  "heightCm": 0,
  "dimensionalWeightKg": 0,
  "deviceId": "string",
  "packageNumber": "string"
}
```

`weightKg/lengthCm/widthCm/heightCm` must be `>0` and within the backend's
own bounds (weight ≤1000, dimensions ≤500) — the Agent's `UnitConverter`
enforces the same bounds locally before ever sending a request.

Response `201`:

```json
{ "measurementId": "string", "result": "pass" | "mismatch", "requiresEvidence": true }
```

Idempotent by (stationId, packageNumber) as a natural key in addition to the
Idempotency-Key header — resubmitting the same station+package combination
returns the SAME stored result rather than creating a second measurement.

## `POST /api/warehouse/measurements/{id}/evidence`

Headers: `Idempotency-Key: <string>` (required).
Body: `multipart/form-data`, field `photo` (JPEG/PNG/WEBP, ≤8MB, magic-byte
sniffed and cross-checked against the declared MIME type).

Only accepted when the measurement's `result` is `mismatch` — a `409` is
returned otherwise.

Response `200`: `{ ok: true, photoUrl: string }`.

## HTTP status codes the Agent's `GbexApiClient` distinguishes

| Status | Meaning | Agent behavior |
|---|---|---|
| 200/201 | success | parse and act on the body |
| 401/403 | invalid/revoked token (or disabled station — indistinguishable today) | stop retrying aggressively, surface "unauthorized" |
| 404 | order/measurement not found | not retried |
| 409 | conflict (e.g. order already closed) | not retried, requires manual resolution |
| 422 | validation failed | not retried, requires manual resolution |
| 408/429/502/503/504/other | transient | retried via the durable outbox with backoff |
