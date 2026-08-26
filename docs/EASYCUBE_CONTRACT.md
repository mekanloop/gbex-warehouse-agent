# EasyCube Web API contract (transcribed from the manufacturer's guide)

Source: "EasyCube Static Dimensioner Software Guide_V01_EN.pdf" (shared in
the gbex.com.tr website repo, read-only). This document transcribes only
the endpoints `IEasyCubeClient` actually uses.

## Endpoints used

| Endpoint | Purpose |
|---|---|
| `GET /devinfo` | Serial number, model, sensor, firmware version |
| `GET /errorlog` | Array of `{Datetime, Code, Message}` |
| `GET /measure` | Trigger a measurement, no image (server redirects to the same shape as `/cap_measure` in this Agent's simulator; the real device's exact behavior depends on `/meas_config`'s auto-send settings — see note below) |
| `GET /last_measure` | Last measurement, no image |
| `GET /cap_measure` | Trigger a measurement WITH image |
| `GET /last_cap_measure` | Last measurement WITH image |
| `GET /alibi/{packageNumber}` | Re-fetch a specific historical measurement by its package number — the correlation key of last resort |
| `GET /image` | `{ImgBase64}` — last captured image only |

## Exact response field names (real device shape — do not "correct" typos)

```json
{
  "DevID": "00000000",
  "PackageNumber": "410",
  "TimeStamp": "2021-07-26 16:06:27",
  "PackageHeight": 23.5,
  "PackageHeightUnit": "cm",
  "PackageLenght": 17.2,
  "PackageLenghtUnit": "cm",
  "PackageWidth": 8.7,
  "PackageWidthUnit": "cm",
  "PackageWeight": 3.552,
  "PackageWeightUnit": "kg",
  "RealVolume": 0,
  "RealVolumeUnit": "",
  "DimWeight": 0,
  "DimWeightUnit": "kg",
  "DimWeightFactor": 0.877,
  "DimWeightFactorUnit": "kg",
  "DimWeightFactorType": 0,
  "Barcode": null,
  "TareEnabled": false,
  "TareHeight": 0,
  "TareHeightUnit": "DOM",
  "ImgBase64": "<only on /cap_measure, /last_cap_measure, /alibi/{n}>"
}
```

**"PackageLenght" (not "Length") is the device's real spelling.** GBEX's own
`EasyCubeMeasurementResponse` DTO preserves it exactly via
`[JsonPropertyName("PackageLenght")]` — silently "fixing" it would break
deserialization against the physical hardware.

## Flagged assumptions — VERIFY ON PHYSICAL HARDWARE

1. **Timestamp timezone.** `TimeStamp` ("2021-07-26 16:18:04") carries no
   explicit zone. `EasyCubeClient` parses it as the Agent PC's LOCAL time
   (reasonable since the device shares the warehouse LAN/power with the
   Agent, and `/datetime` defaults to `AutoDatetime: true`), converted to
   UTC for staleness comparison. If the real device's clock drifts from the
   Agent PC's clock, or reports a genuinely different zone, staleness
   rejection could misfire — check this on the first real device.
2. **`PackageWeightUnit` example value.** The manufacturer's own
   `/cap_measure` example response shows `"PackageWeightUnit": "cm3"` —
   almost certainly a documentation error (that's a volume unit, not a
   weight unit), but if the REAL device genuinely returns this, every
   measurement will be rejected as `MalformedResponse` (deliberately — see
   `UnitConverterTests` — silently guessing would be worse). Confirm what
   the physical device actually sends for this field.
3. **`/measure` vs `/cap_measure` auto-send behavior.** The guide states
   `/measure` "just triggers the measurement, does not return anything" if
   `Auto Send Measurement` is off — this Agent's `IEasyCubeClient` calls
   `/cap_measure` for the workflow's actual capture step specifically to
   avoid depending on that device-side setting (per the guide, `/cap_measure`
   returns the measurement synchronously when `Auto Send Measurement` OR
   `Auto Send Image` is on — the physical device's actual configuration
   needs confirming before relying on this in the field).
4. **`Barcode` field reliability.** The manufacturer's own example populated
   this field with a stray value ("cm") rather than a real barcode,
   suggesting it is not consistently populated across device modes/firmware.
   `MeasurementCorrelationValidator` treats it as a soft cross-check only
   (ignored entirely when null/empty) — `PackageNumber` is the real
   correlation key. Confirm on real hardware whether barcode-correlation
   mode (`/meas_config` `Mode: 1`) reliably populates this field.
