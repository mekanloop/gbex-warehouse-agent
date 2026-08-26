using Gbex.Warehouse.Agent.Core.Models;

namespace Gbex.Warehouse.Agent.Core.Abstractions;

public abstract record EasyCubeResult
{
    public sealed record Success : EasyCubeResult;
    public sealed record DeviceError(string Code, string Message) : EasyCubeResult;
    public sealed record MalformedResponse(string Detail) : EasyCubeResult;
    public sealed record Timeout : EasyCubeResult;
    public sealed record Unreachable(string Detail) : EasyCubeResult;
}

public sealed record DeviceInfo
{
    public required string SerialNumber { get; init; }
    public required string DeviceModel { get; init; }
    public required string Year { get; init; }
    public required string Sensor { get; init; }
    public required string SoftwareVersion { get; init; }
    public required string Mdmi { get; init; }
}

public sealed record DeviceHealth : EasyCubeResult
{
    public static DeviceHealth Healthy(DeviceInfo info) => new() { Info = info };
    public DeviceInfo? Info { get; init; }
}

public sealed record MeasurementOutcome : EasyCubeResult
{
    public static MeasurementOutcome Ok(CapturedMeasurement measurement) => new() { Measurement = measurement };
    public CapturedMeasurement? Measurement { get; init; }
}

public sealed record ImageOutcome : EasyCubeResult
{
    public static ImageOutcome Ok(byte[] bytes, string mimeType) => new() { Bytes = bytes, MimeType = mimeType };
    public byte[]? Bytes { get; init; }
    public string? MimeType { get; init; }
}

/// <summary>
/// Clean abstraction over the manufacturer's documented EasyCube Web API
/// (EasyCube Static Dimensioner Software Guide — see docs/EASYCUBE_CONTRACT.md
/// for the exact field names, including the device's own "PackageLenght"
/// typo, which the Infrastructure implementation preserves faithfully in its
/// wire DTOs rather than silently "fixing"). All HTTP calls to the device
/// live behind this interface — nothing else in the Agent (least of all the
/// WPF UI) issues its own request to the device.
/// </summary>
public interface IEasyCubeClient
{
    Task<EasyCubeResult> GetDeviceInfoAsync(CancellationToken ct);

    Task<EasyCubeResult> GetErrorLogAsync(CancellationToken ct);

    /// <summary>Triggers a fresh capture (/cap_measure) and returns it with its temporary image, if any.</summary>
    Task<EasyCubeResult> CaptureMeasurementAsync(CancellationToken ct);

    /// <summary>Returns the last measurement without triggering a new capture (/last_measure) — used to detect a stale/already-consumed reading.</summary>
    Task<EasyCubeResult> GetLastMeasurementAsync(CancellationToken ct);

    /// <summary>Queries a specific historical measurement by package number (/alibi/{packageNumber}) — used to re-fetch a capture by its correlation key rather than trusting "last" after any delay.</summary>
    Task<EasyCubeResult> GetByPackageNumberAsync(string packageNumber, CancellationToken ct);
}
