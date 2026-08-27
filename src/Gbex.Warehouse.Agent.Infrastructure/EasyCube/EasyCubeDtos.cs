using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gbex.Warehouse.Agent.Infrastructure.EasyCube;

/// <summary>
/// Wire DTOs matching the EasyCube Web API EXACTLY as documented in the
/// manufacturer's "EasyCube Static Dimensioner Software Guide" (see
/// docs/EASYCUBE_CONTRACT.md for the full field reference this was
/// transcribed from). Field names are preserved faithfully, including the
/// device's own "PackageLenght" spelling (not "Length") — this is not a
/// typo in this codebase, it is what the real device returns, and silently
/// "fixing" it here would break deserialization against the physical
/// hardware.
/// </summary>
public sealed class EasyCubeMeasurementResponse
{
    [JsonPropertyName("DevID")] public string? DevId { get; set; }
    [JsonPropertyName("PackageNumber")] public string? PackageNumber { get; set; }
    [JsonPropertyName("TimeStamp")] public string? TimeStamp { get; set; }
    [JsonPropertyName("PackageHeight")] public double PackageHeight { get; set; }
    [JsonPropertyName("PackageHeightUnit")] public string? PackageHeightUnit { get; set; }
    // FLAGGED FINDING (confirmed on real hardware, 2026-08-27): the
    // manufacturer's guide documents "PackageLenght" (their own typo), and
    // that is what this codebase originally assumed universally. A real
    // device's /alibi and /last_cap_measure responses were observed
    // returning the CORRECTLY spelled "PackageLength" instead — spelling
    // apparently varies by firmware/unit. Both are accepted; whichever the
    // response actually contains wins (the other defaults to 0/null and is
    // ignored). This is exactly why relying on JsonPropertyName alone had
    // silently turned every real measurement into a MalformedResponse
    // (length parsed as 0 -> OutOfRange) — including the ones carrying a
    // perfectly good evidence photo, which got discarded along with it.
    [JsonPropertyName("PackageLenght")] public double PackageLenghtTypoSpelling { get; set; }
    [JsonPropertyName("PackageLength")] public double? PackageLengthCorrectSpelling { get; set; }
    [JsonIgnore] public double PackageLenght => PackageLenghtTypoSpelling != 0 ? PackageLenghtTypoSpelling : (PackageLengthCorrectSpelling ?? 0);

    [JsonPropertyName("PackageLenghtUnit")] public string? PackageLenghtUnitTypoSpelling { get; set; }
    [JsonPropertyName("PackageLengthUnit")] public string? PackageLengthUnitCorrectSpelling { get; set; }
    [JsonIgnore] public string? PackageLenghtUnit => PackageLenghtUnitTypoSpelling ?? PackageLengthUnitCorrectSpelling;
    [JsonPropertyName("PackageWidth")] public double PackageWidth { get; set; }
    [JsonPropertyName("PackageWidthUnit")] public string? PackageWidthUnit { get; set; }
    [JsonPropertyName("PackageWeight")] public double PackageWeight { get; set; }
    [JsonPropertyName("PackageWeightUnit")] public string? PackageWeightUnit { get; set; }
    [JsonPropertyName("RealVolume")] public double RealVolume { get; set; }
    [JsonPropertyName("RealVolumeUnit")] public string? RealVolumeUnit { get; set; }
    [JsonPropertyName("DimWeight")] public double DimWeight { get; set; }
    [JsonPropertyName("DimWeightUnit")] public string? DimWeightUnit { get; set; }
    [JsonPropertyName("DimWeightFactor")] public double DimWeightFactor { get; set; }
    [JsonPropertyName("DimWeightFactorUnit")] public string? DimWeightFactorUnit { get; set; }
    // FLAGGED FINDING (confirmed on real hardware, 2026-08-27): the
    // manufacturer's own example shows this as an int (0), but a real
    // device returned a STRING ("DOM") here — System.Text.Json throws on a
    // type mismatch, which (same failure mode as the PackageLength
    // spelling above) turns the ENTIRE response into an undeserializable
    // MalformedResponse, discarding a good evidence photo along with it.
    // Never consumed elsewhere in this codebase, so JsonElement (accepts
    // any valid JSON token without throwing) is a safe, minimal fix.
    [JsonPropertyName("DimWeightFactorType")] public JsonElement DimWeightFactorType { get; set; }
    /// <summary>The device's own barcode field. In the manufacturer's own example this was populated with a stray unit string ("cm"), suggesting the field is not reliably populated on every configuration/mode — treated only as a soft cross-check against the operator's scan, never as the primary correlation key (PackageNumber is).</summary>
    [JsonPropertyName("Barcode")] public string? Barcode { get; set; }
    [JsonPropertyName("TareEnabled")] public bool TareEnabled { get; set; }
    [JsonPropertyName("TareHeight")] public double TareHeight { get; set; }
    [JsonPropertyName("TareHeightUnit")] public string? TareHeightUnit { get; set; }
    /// <summary>Present only on /cap_measure, /last_cap_measure, /alibi/{n} — null on /measure, /last_measure.</summary>
    [JsonPropertyName("ImgBase64")] public string? ImgBase64 { get; set; }
}

public sealed class EasyCubeImageResponse
{
    [JsonPropertyName("ImgBase64")] public string? ImgBase64 { get; set; }
}

public sealed class EasyCubeDeviceInfoResponse
{
    [JsonPropertyName("SerialNumber")] public string? SerialNumber { get; set; }
    [JsonPropertyName("DeviceModel")] public string? DeviceModel { get; set; }
    [JsonPropertyName("Year")] public string? Year { get; set; }
    [JsonPropertyName("Sensor")] public string? Sensor { get; set; }
    [JsonPropertyName("SoftwareVersion")] public string? SoftwareVersion { get; set; }
    [JsonPropertyName("MDMI")] public string? Mdmi { get; set; }
}

public sealed class EasyCubeErrorLogEntry
{
    [JsonPropertyName("Datetime")] public string? Datetime { get; set; }
    [JsonPropertyName("Code")] public string? Code { get; set; }
    [JsonPropertyName("Message")] public string? Message { get; set; }
}

public sealed class EasyCubeErrorResponse
{
    [JsonPropertyName("error")] public string? Error { get; set; }
}
