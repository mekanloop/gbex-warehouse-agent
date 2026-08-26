using System.Text.Json;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Models;
using Gbex.Warehouse.Agent.Infrastructure.Retry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gbex.Warehouse.Agent.Infrastructure.Outbox;

/// <summary>
/// Background worker that drains the durable outbox in deterministic order
/// (oldest Id first — see SqliteOutboxStore.ClaimNextAsync). Bounded
/// retry/backoff; 401/403 stop retrying and require operator
/// reconfiguration; 409/422 are treated as permanent (the backend will
/// never accept this exact payload) and require manual resolution rather
/// than being retried forever. A successful replay marks the item
/// Completed — it is not deleted outright, so support has a record of what
/// was queued, but it will never be claimed again.
/// </summary>
public sealed class OutboxProcessor : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);
    private const int MaxRetries = 20;

    private readonly IOutboxStore _outbox;
    private readonly IGbexApiClient _gbexClient;
    private readonly IEvidenceImageStore _imageStore;
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly Random _random = new();

    public OutboxProcessor(IOutboxStore outbox, IGbexApiClient gbexClient, IEvidenceImageStore imageStore, ILogger<OutboxProcessor> logger)
    {
        _outbox = outbox;
        _gbexClient = gbexClient;
        _imageStore = imageStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            OutboxItem? item;
            try
            {
                item = await _outbox.ClaimNextAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (item is null)
            {
                await Delay(PollInterval, stoppingToken);
                continue;
            }

            await ProcessAsync(item, stoppingToken);
        }
    }

    private async Task ProcessAsync(OutboxItem item, CancellationToken ct)
    {
        try
        {
            var outcome = item.OperationType switch
            {
                OutboxOperationType.SubmitMeasurement => await ReplaySubmitMeasurementAsync(item, ct),
                OutboxOperationType.UploadEvidence => await ReplayUploadEvidenceAsync(item, ct),
                _ => new GbexApiResult.ValidationFailed("unknown operation type"),
            };

            await HandleOutcomeAsync(item, outcome, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Outbox item {Id} threw unexpectedly", item.Id);
            await RescheduleAsync(item, "unexpected_exception", ct);
        }
    }

    private async Task<GbexApiResult> ReplaySubmitMeasurementAsync(OutboxItem item, CancellationToken ct)
    {
        var submission = JsonSerializer.Deserialize<MeasurementSubmission>(item.SanitizedPayloadJson)
            ?? throw new InvalidOperationException("Outbox payload failed to deserialize");
        // SAME idempotency key as the original attempt — never regenerated.
        return await _gbexClient.SubmitMeasurementAsync(submission, item.IdempotencyKey, ct);
    }

    private async Task<GbexApiResult> ReplayUploadEvidenceAsync(OutboxItem item, CancellationToken ct)
    {
        if (item.EvidenceFilePath is null || item.MeasurementId is null)
        {
            return new GbexApiResult.ValidationFailed("evidence outbox item missing file path or measurement id");
        }

        byte[] bytes;
        try
        {
            bytes = await _imageStore.ReadAsync(item.EvidenceFilePath, ct);
        }
        catch (FileNotFoundException)
        {
            // The temp file is gone (crash between save and upload, or it
            // was already purged as abandoned) — nothing left to retry.
            return new GbexApiResult.ValidationFailed("evidence file no longer exists");
        }

        var result = await _gbexClient.UploadEvidenceAsync(item.MeasurementId, bytes, "image/jpeg", item.IdempotencyKey, ct);
        if (result is EvidenceUploadOutcome)
        {
            await _imageStore.DeleteAsync(item.EvidenceFilePath, ct);
        }
        return result;
    }

    private async Task HandleOutcomeAsync(OutboxItem item, GbexApiResult outcome, CancellationToken ct)
    {
        switch (outcome)
        {
            case MeasurementSubmitOutcome or EvidenceUploadOutcome or HeartbeatOutcome or OrderLookupOutcome:
                await _outbox.MarkCompletedAsync(item.Id, ct);
                break;

            case GbexApiResult.Unauthorized:
            case GbexApiResult.StationDisabled:
                // Never retry indefinitely on an auth failure.
                await _outbox.MarkRequiresReauthorizationAsync(item.Id, "unauthorized_or_disabled", ct);
                break;

            case GbexApiResult.Conflict conflict:
                await _outbox.MarkRequiresManualResolutionAsync(item.Id, Sanitize(conflict.Message), ct);
                break;

            case GbexApiResult.ValidationFailed validationFailed:
                await _outbox.MarkRequiresManualResolutionAsync(item.Id, Sanitize(validationFailed.Message), ct);
                break;

            case GbexApiResult.NotFound notFound:
                // The order itself is gone/renamed — not something a retry fixes.
                await _outbox.MarkRequiresManualResolutionAsync(item.Id, Sanitize(notFound.Message), ct);
                break;

            case GbexApiResult.TransientFailure transient:
                await RescheduleAsync(item, Sanitize(transient.Message), ct);
                break;

            default:
                await RescheduleAsync(item, "unknown_outcome", ct);
                break;
        }
    }

    private async Task RescheduleAsync(OutboxItem item, string sanitizedError, CancellationToken ct)
    {
        if (item.RetryCount >= MaxRetries)
        {
            _logger.LogError("Outbox item {Id} exceeded max retries ({MaxRetries}) — requires manual resolution", item.Id, MaxRetries);
            await _outbox.MarkRequiresManualResolutionAsync(item.Id, $"max_retries_exceeded:{sanitizedError}", ct);
            return;
        }

        var backoff = BackoffPolicy.Compute(item.RetryCount + 1, BaseBackoff, MaxBackoff, _random);
        await _outbox.MarkFailedAndRescheduleAsync(item.Id, sanitizedError, DateTimeOffset.UtcNow.Add(backoff), ct);
    }

    /// <summary>Backend error messages are Turkish, human-readable, and already free of secrets — but truncate defensively so nothing unbounded ever lands in a log/column.</summary>
    private static string Sanitize(string message) => message.Length > 200 ? message[..200] : message;

    private static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }
}
