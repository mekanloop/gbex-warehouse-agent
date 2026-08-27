namespace Gbex.Warehouse.Agent.Infrastructure.Diagnostics;

/// <summary>
/// Everything a support diagnosis needs — and, by construction, NOTHING
/// else. There is deliberately no field here that could hold the station
/// secret, a customer name/address, a carrier tracking number, or a price:
/// the type itself is the guard, not just the code that renders it (see
/// DiagnosticsReportTests.cs — it asserts this shape, not just the output
/// text). Non-secret configuration (server addresses) is shown as the
/// actual value, since that is genuinely useful for diagnosing "which GBEX
/// environment is this pointed at" and carries no sensitivity; the station
/// secret is represented ONLY as a boolean presence flag, never a value.
/// </summary>
public sealed record DiagnosticsReport
{
    public required string AgentVersion { get; init; }
    public required DateTimeOffset GeneratedAtUtc { get; init; }

    public required string GbexApiBaseUrl { get; init; }
    public required string GbexConnectionState { get; init; }
    public DateTimeOffset? LastSuccessfulHeartbeatAtUtc { get; init; }
    public required bool StationSecretConfigured { get; init; }

    public required string EasyCubeBaseUrl { get; init; }
    public required string EasyCubeConnectionState { get; init; }
    public string? EasyCubeDeviceModel { get; init; }
    public string? EasyCubeSoftwareVersion { get; init; }
    public string? DeviceId { get; init; }

    public required int OfflineQueueCount { get; init; }
    public required int RequiresReauthorizationCount { get; init; }
    public required int RequiresManualResolutionCount { get; init; }

    /// <summary>Already-sanitized error strings as stored by OutboxProcessor (see OutboxItem.LastSanitizedError) — never a raw exception, never a header value.</summary>
    public required IReadOnlyList<string> RecentSanitizedErrors { get; init; }
}
