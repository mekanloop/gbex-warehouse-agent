using Gbex.Warehouse.Agent.Core.Models;

namespace Gbex.Warehouse.Agent.Core.Abstractions;

public abstract record GbexApiResult
{
    public sealed record Success : GbexApiResult;
    public sealed record Unauthorized : GbexApiResult;
    /// <summary>The station exists and authenticates, but is disabled — distinct from Unauthorized (bad/revoked token) so the UI can show the right message.</summary>
    public sealed record StationDisabled : GbexApiResult;
    public sealed record NotFound(string Message) : GbexApiResult;
    public sealed record Conflict(string Message) : GbexApiResult;
    public sealed record ValidationFailed(string Message) : GbexApiResult;
    /// <summary>Network/timeout/5xx — safe to retry.</summary>
    public sealed record TransientFailure(string Message) : GbexApiResult;
}

public sealed record HeartbeatOutcome : GbexApiResult
{
    public static HeartbeatOutcome Ok(string stationName) => new() { StationName = stationName };
    public string? StationName { get; init; }
}

public sealed record OrderLookupOutcome : GbexApiResult
{
    public static OrderLookupOutcome Ok(StationOrderDto order) => new() { Order = order };
    public StationOrderDto? Order { get; init; }
}

public sealed record MeasurementSubmitOutcome : GbexApiResult
{
    public static MeasurementSubmitOutcome Ok(MeasurementSubmissionResult result) => new() { Result = result };
    public MeasurementSubmissionResult? Result { get; init; }
}

public sealed record EvidenceUploadOutcome : GbexApiResult
{
    public static EvidenceUploadOutcome Ok(string photoUrl) => new() { PhotoUrl = photoUrl };
    public string? PhotoUrl { get; init; }
}

/// <summary>
/// Typed client for GBEX's machine-authenticated warehouse routes. Exact
/// contracts from app/api/warehouse/* in the gbex website repo — see
/// docs/GBEX_API_CONTRACT.md. This is the ONLY place that talks to GBEX;
/// nothing else in the Agent constructs a request to it directly.
/// </summary>
public interface IGbexApiClient
{
    Task<GbexApiResult> HeartbeatAsync(string agentVersion, CancellationToken ct);

    Task<GbexApiResult> LookupOrderAsync(string barcode, CancellationToken ct);

    /// <summary>idempotencyKey must be the SAME value on every retry of the same logical submission — see Core.Idempotency.</summary>
    Task<GbexApiResult> SubmitMeasurementAsync(MeasurementSubmission submission, string idempotencyKey, CancellationToken ct);

    /// <summary>imageBytes for a MISMATCH measurement only — the backend rejects evidence for a pass result.</summary>
    Task<GbexApiResult> UploadEvidenceAsync(string measurementId, byte[] imageBytes, string mimeType, string idempotencyKey, CancellationToken ct);
}
