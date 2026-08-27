namespace Gbex.Warehouse.Agent.Core.Abstractions;

public enum OutboxOperationType
{
    SubmitMeasurement,
    UploadEvidence,
}

public enum OutboxItemState
{
    Pending,
    InProgress,
    Completed,
    Failed,
    /// <summary>401/403 — never auto-retried further; requires operator action (re-configure station).</summary>
    RequiresReauthorization,
    /// <summary>422/409 the backend will never accept as-is — retrying is pointless.</summary>
    RequiresManualResolution,
}

/// <summary>One durable outbox row — everything needed to safely retry, and nothing that must never be persisted (no station secret, ever).</summary>
public sealed record OutboxItem
{
    public required long Id { get; init; }
    public required OutboxOperationType OperationType { get; init; }
    public required string GbexBarcode { get; init; }
    public string? MeasurementId { get; init; }
    public string? EasyCubePackageNumber { get; init; }
    public string? DeviceId { get; init; }
    /// <summary>Generated once per logical operation, reused on every retry — never regenerated merely because a prior attempt timed out.</summary>
    public required string IdempotencyKey { get; init; }
    /// <summary>Sanitized JSON payload — no secrets, no full customer PII beyond what the submission itself already carries (barcode, measured facts).</summary>
    public required string SanitizedPayloadJson { get; init; }
    public required int RetryCount { get; init; }
    public required DateTimeOffset NextAttemptAt { get; init; }
    public string? LastSanitizedError { get; init; }
    /// <summary>Path to a temporary evidence file this operation still needs to upload — null once uploaded/deleted.</summary>
    public string? EvidenceFilePath { get; init; }
    public required OutboxItemState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record NewOutboxItem
{
    public required OutboxOperationType OperationType { get; init; }
    public required string GbexBarcode { get; init; }
    public string? MeasurementId { get; init; }
    public string? EasyCubePackageNumber { get; init; }
    public string? DeviceId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string SanitizedPayloadJson { get; init; }
    public string? EvidenceFilePath { get; init; }
}

/// <summary>
/// Durable local outbox (SQLite in the real Infrastructure implementation).
/// Guarantees: one Idempotency-Key per logical operation persists across
/// restarts; concurrent workers cannot double-process the same row (via
/// ClaimNextAsync's atomic claim, not a plain SELECT-then-UPDATE).
/// </summary>
public interface IOutboxStore
{
    /// <summary>Idempotent enqueue: if an item with this IdempotencyKey already exists, returns the existing row instead of creating a duplicate.</summary>
    Task<OutboxItem> EnqueueAsync(NewOutboxItem item, CancellationToken ct);

    /// <summary>Atomically claims the next Pending item whose NextAttemptAt has arrived, flipping it to InProgress in the same operation — the mechanism that makes concurrent workers safe. Returns null if none are ready.</summary>
    Task<OutboxItem?> ClaimNextAsync(CancellationToken ct);

    Task MarkCompletedAsync(long id, CancellationToken ct);

    Task MarkFailedAndRescheduleAsync(long id, string sanitizedError, DateTimeOffset nextAttemptAt, CancellationToken ct);

    Task MarkRequiresReauthorizationAsync(long id, string sanitizedError, CancellationToken ct);

    Task MarkRequiresManualResolutionAsync(long id, string sanitizedError, CancellationToken ct);

    Task<int> CountPendingAsync(CancellationToken ct);

    /// <summary>Count of items sitting in one specific terminal-ish state — used by the diagnostics report (e.g. how many need reauthorization or manual resolution right now).</summary>
    Task<int> CountByStateAsync(OutboxItemState state, CancellationToken ct);

    /// <summary>Most recent sanitized error strings across every item, newest first — for the diagnostics export only. Never returns SanitizedPayloadJson or anything else that could carry customer facts beyond what was already in a submission.</summary>
    Task<IReadOnlyList<string>> GetRecentSanitizedErrorsAsync(int limit, CancellationToken ct);

    Task<OutboxItem?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct);
}
