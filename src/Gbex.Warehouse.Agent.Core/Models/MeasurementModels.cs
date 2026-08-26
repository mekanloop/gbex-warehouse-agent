namespace Gbex.Warehouse.Agent.Core.Models;

/// <summary>Raw hardware facts only — exactly what POST /api/warehouse/measurements accepts. Never a business decision (no PASS/MISMATCH computed here; the backend decides).</summary>
public sealed record MeasurementSubmission
{
    public required string Barcode { get; init; }
    public required decimal WeightKg { get; init; }
    public required decimal LengthCm { get; init; }
    public required decimal WidthCm { get; init; }
    public required decimal HeightCm { get; init; }
    public decimal? DimensionalWeightKg { get; init; }
    public string? DeviceId { get; init; }
    public string? PackageNumber { get; init; }
}

public enum MeasurementResultKind
{
    Pass,
    Mismatch,
}

/// <summary>Response shape from POST /api/warehouse/measurements.</summary>
public sealed record MeasurementSubmissionResult
{
    public required string MeasurementId { get; init; }
    public required MeasurementResultKind Result { get; init; }
    public required bool RequiresEvidence { get; init; }
}

/// <summary>A single physical measurement captured from EasyCube, already unit-normalized to KG/CM by the Infrastructure layer's EasyCube client — Core never deals with raw device units.</summary>
public sealed record CapturedMeasurement
{
    public required string DeviceId { get; init; }
    public required string PackageNumber { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required decimal WeightKg { get; init; }
    public required decimal LengthCm { get; init; }
    public required decimal WidthCm { get; init; }
    public required decimal HeightCm { get; init; }
    public decimal? DimensionalWeightKg { get; init; }
    /// <summary>The barcode the EasyCube device itself associated with this capture, if it has its own scanner in barcode-correlation mode. Empty when the device was triggered in a mode with no barcode of its own — correlation then relies solely on package number + our own scanned barcode.</summary>
    public string? DeviceReportedBarcode { get; init; }
    /// <summary>Present only for a captured/cap_measure or alibi read — base64 image bytes, held in memory just long enough to write to a temp file.</summary>
    public string? ImageBase64 { get; init; }
}
