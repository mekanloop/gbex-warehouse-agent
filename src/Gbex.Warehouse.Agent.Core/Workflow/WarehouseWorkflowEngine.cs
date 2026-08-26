using System.Text.Json;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Barcode;
using Gbex.Warehouse.Agent.Core.Correlation;
using Gbex.Warehouse.Agent.Core.Idempotency;
using Gbex.Warehouse.Agent.Core.Models;
using Microsoft.Extensions.Logging;

namespace Gbex.Warehouse.Agent.Core.Workflow;

public abstract record LookupOutcome
{
    public sealed record Found(StationOrderDto Order) : LookupOutcome;
    public sealed record InvalidBarcode(string Reason) : LookupOutcome;
    public sealed record NotFound(string Message) : LookupOutcome;
    public sealed record Offline : LookupOutcome;
    public sealed record Unauthorized : LookupOutcome;
    public sealed record StationDisabled : LookupOutcome;
}

public abstract record MeasureOutcome
{
    public sealed record Pass(string MeasurementId) : MeasureOutcome;
    public sealed record Mismatch(string MeasurementId, bool EvidenceUploaded) : MeasureOutcome;
    public sealed record QueuedOffline(string Reason) : MeasureOutcome;
    public sealed record EasyCubeFailure(string Reason) : MeasureOutcome;
    public sealed record CorrelationRejected(CorrelationResult Reason) : MeasureOutcome;
    public sealed record Rejected(string Reason) : MeasureOutcome;
}

/// <summary>
/// The workflow core: IDLE -> scan -> lookup -> display -> measure ->
/// correlate -> submit -> PASS/MISMATCH. Contains zero HttpClient, zero
/// SQLite, zero WPF/Windows references — everything it needs is injected as
/// an interface, so this class is fully unit-testable with fakes. This is
/// deliberately the ONLY place that decides what happens next in the
/// workflow; the WPF layer only calls into this and renders whatever state
/// it reports back.
///
/// It never computes PASS/MISMATCH itself and never touches customer funds,
/// selects a shipping provider, or resolves a mismatch — the backend's response IS the
/// decision; this class only acts on it (delete image on pass, upload
/// evidence on mismatch).
/// </summary>
public sealed class WarehouseWorkflowEngine
{
    private readonly IGbexApiClient _gbexClient;
    private readonly IEasyCubeClient _easyCubeClient;
    private readonly IEvidenceImageStore _imageStore;
    private readonly IOutboxStore _outbox;
    private readonly IIdempotencyKeyGenerator _keyGenerator;
    private readonly MeasurementCorrelationValidator _correlation;
    private readonly ILogger<WarehouseWorkflowEngine> _logger;

    public AgentWorkflowState State { get; private set; } = AgentWorkflowState.Ready;
    public event Action<AgentWorkflowState>? StateChanged;

    public WarehouseWorkflowEngine(
        IGbexApiClient gbexClient,
        IEasyCubeClient easyCubeClient,
        IEvidenceImageStore imageStore,
        IOutboxStore outbox,
        IIdempotencyKeyGenerator keyGenerator,
        MeasurementCorrelationValidator correlation,
        ILogger<WarehouseWorkflowEngine> logger)
    {
        _gbexClient = gbexClient;
        _easyCubeClient = easyCubeClient;
        _imageStore = imageStore;
        _outbox = outbox;
        _keyGenerator = keyGenerator;
        _correlation = correlation;
        _logger = logger;
    }

    private void SetState(AgentWorkflowState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public async Task<LookupOutcome> ScanAndLookupAsync(string rawBarcodeInput, CancellationToken ct)
    {
        var normalized = BarcodeNormalizer.Normalize(rawBarcodeInput);
        if (normalized is not BarcodeNormalizationResult.Valid valid)
        {
            var reason = normalized switch
            {
                BarcodeNormalizationResult.Empty => "Barkod boş.",
                BarcodeNormalizationResult.TooLong tooLong => $"Barkod çok uzun ({tooLong.Length} karakter).",
                BarcodeNormalizationResult.InvalidFormat invalid => $"Geçersiz barkod biçimi: {invalid.Value}",
                _ => "Geçersiz barkod.",
            };
            return new LookupOutcome.InvalidBarcode(reason);
        }

        SetState(AgentWorkflowState.LookingUpOrder);
        var result = await _gbexClient.LookupOrderAsync(valid.Barcode, ct);

        switch (result)
        {
            case OrderLookupOutcome ok when ok.Order is not null:
                SetState(AgentWorkflowState.OrderFound);
                return new LookupOutcome.Found(ok.Order);
            case GbexApiResult.NotFound notFound:
                SetState(AgentWorkflowState.Ready);
                return new LookupOutcome.NotFound(notFound.Message);
            case GbexApiResult.Unauthorized:
                SetState(AgentWorkflowState.StationUnauthorized);
                return new LookupOutcome.Unauthorized();
            case GbexApiResult.StationDisabled:
                SetState(AgentWorkflowState.StationDisabled);
                return new LookupOutcome.StationDisabled();
            case GbexApiResult.TransientFailure:
                SetState(AgentWorkflowState.OfflineQueued);
                return new LookupOutcome.Offline();
            default:
                SetState(AgentWorkflowState.Ready);
                return new LookupOutcome.NotFound("Bilinmeyen bir hata oluştu.");
        }
    }

    public async Task<MeasureOutcome> MeasureAndSubmitAsync(StationOrderDto order, CancellationToken ct)
    {
        SetState(AgentWorkflowState.Measuring);

        var capture = await _easyCubeClient.CaptureMeasurementAsync(ct);
        if (capture is not MeasurementOutcome measurementOutcome || measurementOutcome.Measurement is null)
        {
            SetState(AgentWorkflowState.EasyCubeError);
            var reason = DescribeEasyCubeFailure(capture);
            _logger.LogWarning("EasyCube capture failed for barcode {Barcode}: {Reason}", order.GbexBarcode, reason);
            return new MeasureOutcome.EasyCubeFailure(reason);
        }

        var measurement = measurementOutcome.Measurement;
        var correlationResult = _correlation.Validate(order.GbexBarcode, measurement);
        if (correlationResult is not CorrelationResult.Valid)
        {
            SetState(AgentWorkflowState.EasyCubeError);
            _logger.LogWarning("Correlation rejected for barcode {Barcode}: {Result}", order.GbexBarcode, correlationResult);
            return new MeasureOutcome.CorrelationRejected(correlationResult);
        }

        SetState(AgentWorkflowState.Submitting);

        string? imageHandle = null;
        if (!string.IsNullOrEmpty(measurement.ImageBase64))
        {
            try
            {
                var bytes = Convert.FromBase64String(measurement.ImageBase64);
                imageHandle = await _imageStore.SaveTemporaryAsync(bytes, "image/jpeg", ct);
            }
            catch (FormatException)
            {
                _logger.LogWarning("EasyCube returned a malformed image payload for package {PackageNumber} — continuing without evidence image", measurement.PackageNumber);
            }
        }

        var submission = new MeasurementSubmission
        {
            Barcode = order.GbexBarcode,
            WeightKg = measurement.WeightKg,
            LengthCm = measurement.LengthCm,
            WidthCm = measurement.WidthCm,
            HeightCm = measurement.HeightCm,
            DimensionalWeightKg = measurement.DimensionalWeightKg,
            DeviceId = measurement.DeviceId,
            PackageNumber = measurement.PackageNumber,
        };

        var idempotencyKey = _keyGenerator.NewKey();
        var submitResult = await _gbexClient.SubmitMeasurementAsync(submission, idempotencyKey, ct);

        switch (submitResult)
        {
            case MeasurementSubmitOutcome ok when ok.Result is not null:
                _correlation.MarkConsumed(measurement.PackageNumber);
                return await HandleSubmitSuccessAsync(ok.Result, imageHandle, submission, idempotencyKey, ct);

            case GbexApiResult.Unauthorized:
                SetState(AgentWorkflowState.StationUnauthorized);
                if (imageHandle is not null) await _imageStore.DeleteAsync(imageHandle, ct);
                return new MeasureOutcome.Rejected("İstasyon yetkisi geçersiz.");

            case GbexApiResult.StationDisabled:
                SetState(AgentWorkflowState.StationDisabled);
                if (imageHandle is not null) await _imageStore.DeleteAsync(imageHandle, ct);
                return new MeasureOutcome.Rejected("İstasyon devre dışı.");

            case GbexApiResult.ValidationFailed validationFailed:
                SetState(AgentWorkflowState.Ready);
                if (imageHandle is not null) await _imageStore.DeleteAsync(imageHandle, ct);
                return new MeasureOutcome.Rejected(validationFailed.Message);

            case GbexApiResult.Conflict conflict:
                SetState(AgentWorkflowState.Ready);
                if (imageHandle is not null) await _imageStore.DeleteAsync(imageHandle, ct);
                return new MeasureOutcome.Rejected(conflict.Message);

            case GbexApiResult.TransientFailure:
                // Network trouble — queue durably rather than lose the
                // measurement. The image (if any) stays on disk, referenced
                // by the outbox row, until the retry succeeds.
                await EnqueueOfflineAsync(order, submission, idempotencyKey, imageHandle, ct);
                SetState(AgentWorkflowState.OfflineQueued);
                return new MeasureOutcome.QueuedOffline("Bağlantı yok — ölçüm kuyruğa alındı.");

            default:
                SetState(AgentWorkflowState.Ready);
                if (imageHandle is not null) await _imageStore.DeleteAsync(imageHandle, ct);
                return new MeasureOutcome.Rejected("Bilinmeyen bir hata oluştu.");
        }
    }

    private async Task<MeasureOutcome> HandleSubmitSuccessAsync(
        MeasurementSubmissionResult result,
        string? imageHandle,
        MeasurementSubmission submission,
        string idempotencyKey,
        CancellationToken ct)
    {
        if (result.Result == MeasurementResultKind.Pass)
        {
            // PASS: delete the temporary image immediately — never held
            // longer than it takes to confirm the backend's verdict.
            if (imageHandle is not null)
            {
                await _imageStore.DeleteAsync(imageHandle, ct);
            }

            SetState(AgentWorkflowState.VerifiedPass);
            return new MeasureOutcome.Pass(result.MeasurementId);
        }

        // MISMATCH: upload evidence once, confirm, then delete. If the
        // upload itself fails, the durable outbox owns the retry — this
        // method does not loop or block on it.
        var evidenceUploaded = false;
        if (result.RequiresEvidence && imageHandle is not null)
        {
            var evidenceKey = _keyGenerator.NewKey();
            try
            {
                var bytes = await _imageStore.ReadAsync(imageHandle, ct);
                var uploadResult = await _gbexClient.UploadEvidenceAsync(result.MeasurementId, bytes, "image/jpeg", evidenceKey, ct);
                if (uploadResult is EvidenceUploadOutcome)
                {
                    await _imageStore.DeleteAsync(imageHandle, ct);
                    evidenceUploaded = true;
                }
                else if (uploadResult is GbexApiResult.TransientFailure)
                {
                    await EnqueueEvidenceRetryAsync(submission.Barcode, result.MeasurementId, imageHandle, evidenceKey, ct);
                }
                else
                {
                    _logger.LogWarning("Evidence upload for measurement {MeasurementId} was rejected: {Result}", result.MeasurementId, uploadResult);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Evidence upload threw for measurement {MeasurementId}", result.MeasurementId);
                await EnqueueEvidenceRetryAsync(submission.Barcode, result.MeasurementId, imageHandle, evidenceKey, ct);
            }
        }

        SetState(AgentWorkflowState.OnHoldMismatch);
        return new MeasureOutcome.Mismatch(result.MeasurementId, evidenceUploaded);
    }

    private async Task EnqueueOfflineAsync(StationOrderDto order, MeasurementSubmission submission, string idempotencyKey, string? imageHandle, CancellationToken ct)
    {
        var payloadJson = JsonSerializer.Serialize(submission);
        await _outbox.EnqueueAsync(new NewOutboxItem
        {
            OperationType = OutboxOperationType.SubmitMeasurement,
            GbexBarcode = order.GbexBarcode,
            EasyCubePackageNumber = submission.PackageNumber,
            DeviceId = submission.DeviceId,
            IdempotencyKey = idempotencyKey,
            SanitizedPayloadJson = payloadJson,
            EvidenceFilePath = imageHandle,
        }, ct);
    }

    private async Task EnqueueEvidenceRetryAsync(string barcode, string measurementId, string imageHandle, string idempotencyKey, CancellationToken ct)
    {
        await _outbox.EnqueueAsync(new NewOutboxItem
        {
            OperationType = OutboxOperationType.UploadEvidence,
            GbexBarcode = barcode,
            MeasurementId = measurementId,
            IdempotencyKey = idempotencyKey,
            SanitizedPayloadJson = JsonSerializer.Serialize(new { measurementId }),
            EvidenceFilePath = imageHandle,
        }, ct);
    }

    private static string DescribeEasyCubeFailure(EasyCubeResult result) => result switch
    {
        EasyCubeResult.DeviceError err => $"Cihaz hatası [{err.Code}]: {err.Message}",
        EasyCubeResult.MalformedResponse malformed => $"Cihaz geçersiz yanıt döndürdü: {malformed.Detail}",
        EasyCubeResult.Timeout => "Cihaz yanıt vermedi (zaman aşımı).",
        EasyCubeResult.Unreachable unreachable => $"Cihaza ulaşılamıyor: {unreachable.Detail}",
        _ => "Bilinmeyen cihaz hatası.",
    };

    public void ReturnToReady() => SetState(AgentWorkflowState.Ready);
}
