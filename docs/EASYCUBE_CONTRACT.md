# EasyCube contracts (transcribed from the manufacturer's guide)

Source: "EasyCube Static Dimensioner Software Guide_V01_EN.pdf" (shared in
the gbex.com.tr website repo, read-only). The device exposes TWO separate
surfaces documented in this one PDF: a raw TCP/IP socket protocol (pages
3-16, "EasyCube TCP/IP Protocol") and an HTTP Web API (pages 17-29,
"EasyCube Web API"). This file covers both — the TCP protocol is now the
**primary** integration (`EasyCubeTcpListener`/`EasyCubeProtocolZeroParser`),
matching the real wiring: the barcode scanner is plugged into EasyCube's own
USB port, not the PC, and EasyCube pushes a combined barcode+measurement
record over Ethernet whenever it reads one. The HTTP Web API
(`IEasyCubeClient`/`EasyCubeClient`) remains only as the **optional manual
fallback** for a PC-attached keyboard-wedge scanner — never the default.

## EasyCube TCP/IP Protocol (PRIMARY — "Protocol 0")

Selected via `/tcps_config`'s `Protocol` setting: `0` = native EasyCube wire
format (what this Agent speaks), `1`/`2` emulate Cubiscan/QubeVu instead for
other WMS integrations. Symbols: `<CR>` = 0x0D, `<LF>` = 0x0A. Commands are
ASCII text terminated `<CR><LF>`; every response is wrapped in `{...}`.

**MFR — get a full measurement record** (the shape this Agent parses):
```
Command:  MFR<CR><LF>
Response: {MFR,DSN,<serial>,P,<package-number>,T,<yyyy-MM-dd HH:mm:ss>,
           L,<length>,LU,<unit>,W,<width>,WU,<unit>,H,<height>,HU,<unit>,
           V,<volume>,VU,<unit>,WT,<weight>,WTU,<unit>,
           DWT,<dim-weight>,DWTU,<unit>,DWTF,<factor>,DWTFU,<unit>,DWTFT,<type>,
           TAR,<tare-height>,TARU,<unit>,TARF,<flag>,B,<barcode>}
Example:  {M,DSN,00000000,P,1115,T,2021-06-02 13:03:36,L,022.4,LU,cm,
           W,008.9,WU,cm,H,018.1,HU,cm,V,03251.4,VU,cm3,WT,000.000,WTU,kg,
           DWT,000.908,DWTU,kg,DWTF,4000.000,DWTFU,cm3/kg,DWTFT,DOM,
           TAR,000.0,TARU,cm,TARF,DIS,B,000000000}
```
Note the guide's own worked example leads with `{M,...}`, not `{MFR,...}` as
its "Response" row specifies — an inconsistency in the manufacturer's
document. `EasyCubeProtocolZeroParser` accepts both tags, plus `MAR` (the
archived/alibi-record command, identical field set plus an extra `VAL` flag).

**DVM — get device model** (used only by `EasyCubeTcpProbe`'s "Bağlantıyı Test Et" connectivity check, never by the persistent listener):
```
Command:  DVM<CR><LF>
Response: {DVM,<model>}
Example:  {DVM,EasyCube-1.6}
```

**DVI — get device information**: `{DVI,SN,<serial>,M,<model>,Y,<year>,S,<sensor>,V,<version>}`.

**TCPS — get/set the TCP/IP server settings** (configured on the device
itself, e.g. via its own Web UI/API — not by this Agent):
```
Command:  TCPS<CR><LF>
Response: {TCPS,P,<port>,PR,<protocol-type>,IS,<image-scale>,
           DAS,<data-auto-send-enabled>,IAS,<image-auto-send-enabled>,E,<enabled>}
Example:  {TCPS,P,9990,PR,0,IS,25,DAS,1,IAS,0,E,1}
```
`DAS` (Data Auto Send) must be enabled on the device for it to push a
measurement unsolicited — this Agent's persistent connection is a pure
listener and never issues an `MFR` command itself.

### Flagged assumptions — VERIFY ON PHYSICAL HARDWARE

1. **Auto-push frame shape.** The guide documents the `DAS` flag's existence
   but never shows, in narrative text, what an unsolicited push actually
   looks like on the wire. `EasyCubeProtocolZeroParser` assumes it is
   byte-for-byte the same shape as the documented `MFR` pull response — the
   only measurement-record shape defined anywhere in the guide. Confirm
   against a real device with `DAS` enabled.
2. **Leading-tag inconsistency** (`MFR` vs `M`) — see above; both accepted.
3. **Timestamp timezone** — same assumption as the HTTP path below: parsed
   as the Agent PC's local time, converted to UTC. If the device's clock
   differs from the Agent PC's, staleness rejection could misfire.
4. **Barcode field reliability** — same caveat as the HTTP path's `Barcode`
   field: the manufacturer's own worked HTTP example populated it with a
   stray unit string ("cm") rather than a real barcode. In the TCP push
   flow this field is the ONLY correlation key (there is no separate
   operator-scanned value to fall back on), so if it is unreliable in
   practice, `HandleDeviceMeasurementAsync` will report "EasyCube ölçümünde
   barkod yok" for every push — confirm `B` is populated reliably with the
   device in barcode-correlation mode before a physical pilot.
5. **Single client assumption** — the guide does not state whether the
   device's TCP/IP server accepts more than one simultaneous client
   connection. This Agent (and its simulator) assume exactly one.

**No image field.** The `MFR`/`M`/`MAR` records above carry no image data at
all — images are a separate concern (`I`/`MAI` commands, or the HTTP `/image`
endpoint below). Because of this, a mismatch measurement that arrives via the
TCP push has no evidence photo by construction. `WarehouseWorkflowEngine`
handles this by opportunistically calling the OPTIONAL HTTP fallback client's
`GetByPackageNumberAsync` (`/alibi/{packageNumber}`, which DOES return
`ImgBase64`) using the same `PackageNumber` the TCP push reported, the moment
the backend confirms evidence is actually required. If the HTTP link isn't
configured or the device doesn't answer, the mismatch still succeeds — the
result just reports `EvidenceOutcome.Unavailable` instead of a photo, never
blocking on it. Practically: **a mismatch's evidence photo only exists if
BOTH the TCP link (primary) AND the HTTP link (fallback) are configured for
the same physical device.**

## EasyCube Web API (OPTIONAL FALLBACK — HTTP)

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

**"PackageLenght" (not "Length") is the device's documented spelling — but
NOT universal.** A real EasyCube unit (2026-08-27 physical pilot) was
observed returning the CORRECTLY spelled `"PackageLength"` /
`"PackageLengthUnit"` instead — spelling apparently varies by firmware/unit,
contradicting the guide's own example. `EasyCubeMeasurementResponse` accepts
BOTH spellings (separate backing fields, whichever the response actually
populates wins) — before this, an unrecognized spelling silently defaulted
the length to 0, which then failed unit validation and turned the entire
response into a `MalformedResponse`, discarding a perfectly good evidence
photo along with it. If another field is ever found with a similar
spelling mismatch on a different unit, apply the same dual-property pattern
rather than trusting the guide's spelling as gospel.

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
