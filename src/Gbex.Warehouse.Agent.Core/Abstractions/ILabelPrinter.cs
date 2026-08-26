namespace Gbex.Warehouse.Agent.Core.Abstractions;

/// <summary>
/// Printer abstraction for a FUTURE phase. Phase 6 provides only the
/// interface, a configuration model, and a fake/test implementation — it
/// must never retrieve, expose, or print a real carrier label. There is no
/// production implementation of this interface in this repository yet.
/// </summary>
public sealed record PrinterConfiguration
{
    public required string PrinterName { get; init; }
    public required bool Enabled { get; init; }
}

public enum PrinterCapabilityStatus
{
    NotConfigured,
    Ready,
    Offline,
    Error,
}

public sealed record PrinterStatus
{
    public required PrinterCapabilityStatus Status { get; init; }
    public string? Detail { get; init; }
}

public interface ILabelPrinter
{
    Task<PrinterStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Deliberately takes opaque, pre-rendered label bytes it never inspects — this interface has no concept of "carrier label" and Phase 6 never calls this with real label content.</summary>
    Task<bool> PrintAsync(byte[] labelBytes, string mimeType, CancellationToken ct);
}
