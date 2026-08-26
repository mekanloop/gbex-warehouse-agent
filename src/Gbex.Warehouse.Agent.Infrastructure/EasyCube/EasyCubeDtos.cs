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
    [JsonPropertyName("PackageLenght")] public double PackageLenght { get; set; }
    [JsonPropertyName("PackageLenghtUnit")] public string? PackageLenghtUnit { get; set; }
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
    [JsonPropertyName("DimWeightFactorType")] public int DimWeightFactorType { get; set; }
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
