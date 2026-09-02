using System.Text.Json;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Barcode;
using Gbex.Warehouse.Agent.Core.Correlation;
using Gbex.Warehouse.Agent.Core.EasyCube;
using Gbex.Warehouse.Agent.Core.Evidence;
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

/// <summary>Distinguishes "nothing to upload" from "upload failed, retrying" — collapsing both into one bool would let the UI claim a retry is queued when no image was ever available at all.</summary>
public enum EvidenceOutcome
{
    Uploaded,
    QueuedForRetry,
    Unavailable,
}

public abstract record MeasureOutcome
{
    public sealed record Pass(string MeasurementId) : MeasureOutcome;
    public sealed record Mismatch(string MeasurementId, EvidenceOutcome Evidence) : MeasureOutcome;
    public sealed record QueuedOffline(string Reason) : MeasureOutcome;
    public sealed record EasyCubeFailure(string Reason) : MeasureOutcome;
    public sealed record CorrelationRejected(CorrelationResult Reason) : MeasureOutcome;
    public sealed record Rejected(string Reason) : MeasureOutcome;
    /// <summary>The device pushed a measurement, but looking up its barcode failed — only reachable from HandleDeviceMeasurementAsync (the manual keyboard-wedge fallback surfaces lookup failures directly as a LookupOutcome instead, before any measurement is ever taken).</summary>
    public sealed record LookupFailed(LookupOutcome Reason) : MeasureOutcome;
}

/// <summary>Result of the device-push flow: the order, if lookup got far enough to resolve one, plus the final outcome.</summary>
public sealed record DeviceMeasurementResult(StationOrderDto? Order, MeasureOutcome Outcome);

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

        return await LookupOrderInternalAsync(valid.Barcode, ct);
    }

    /// <summary>Shared by the manual (keyboard-wedge fallback) and device-push flows — the barcode is already normalized/validated by the caller.</summary>
    private async Task<LookupOutcome> LookupOrderInternalAsync(string barcode, CancellationToken ct)
    {
        SetState(AgentWorkflowState.LookingUpOrder);
        var result = await _gbexClient.LookupOrderAsync(barcode, ct);

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

    /// <summary>
    /// PRIMARY flow entry point — called whenever EasyCube's own persistent
    /// TCP connection pushes a combined barcode+measurement record (see
    /// IEasyCubeConnection). The barcode scanner is wired into EasyCube
    /// itself, not the PC, so there is no separate "operator scanned this on
    /// the PC" step here: the pushed record's own barcode field IS the
    /// correlation key, used to look up the order AND to validate the
    /// measurement, in one pass.
    /// </summary>
    public async Task<DeviceMeasurementResult> HandleDeviceMeasurementAsync(CapturedMeasurement measurement, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(measurement.DeviceReportedBarcode))
        {
            SetState(AgentWorkflowState.EasyCubeError);
            _logger.LogWarning("EasyCube pushed a measurement (package {PackageNumber}) with no barcode — cannot look up an order", measurement.PackageNumber);
            return new DeviceMeasurementResult(null, new MeasureOutcome.EasyCubeFailure(
                "EasyCube ölçümünde barkod yok — barkod okuyucunun EasyCube'a bağlı olduğundan ve barkod korelasyon modunun açık olduğundan emin olun."));
        }

        var normalized = BarcodeNormalizer.Normalize(measurement.DeviceReportedBarcode);
        if (normalized is not BarcodeNormalizationResult.Valid valid)
        {
            SetState(AgentWorkflowState.EasyCubeError);
            return new DeviceMeasurementResult(null, new MeasureOutcome.EasyCubeFailure(
                $"EasyCube'un okuduğu barkod geçersiz: {measurement.DeviceReportedBarcode}"));
        }

        var lookup = await LookupOrderInternalAsync(valid.Barcode, ct);
        if (lookup is not LookupOutcome.Found found)
        {
            return new DeviceMeasurementResult(null, new MeasureOutcome.LookupFailed(lookup));
        }

        var outcome = await ValidateAndSubmitAsync(found.Order, measurement, ct);
        return new DeviceMeasurementResult(found.Order, outcome);
    }

    /// <summary>FALLBACK flow only — actively pulls a measurement from EasyCube's HTTP Web API after the operator manually scans/types a barcode into the PC. Never the default path; see IEasyCubeConnection/HandleDeviceMeasurementAsync for the primary TCP push flow.</summary>
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

        return await ValidateAndSubmitAsync(order, measurementOutcome.Measurement, ct);
    }

    /// <summary>Shared tail of both flows: correlate, save any evidence image, submit idempotently, handle PASS/MISMATCH. `measurement` is already unit-normalized (KG/CM) and, for the device-push flow, already known to have a barcode — the caller looked the order up using it.</summary>
    private async Task<MeasureOutcome> ValidateAndSubmitAsync(StationOrderDto order, CapturedMeasurement measurement, CancellationToken ct)
    {
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
            var bytes = EasyCubeImageDecoder.TryDecode(measurement.ImageBase64);
            var mimeType = bytes is not null ? ImageFormatSniffer.Sniff(bytes) : null;
            if (bytes is not null && mimeType is not null)
            {
                imageHandle = await _imageStore.SaveTemporaryAsync(bytes, mimeType, ct);
            }
            else
            {
                _logger.LogWarning("EasyCube returned an undecodable or unrecognized-format image payload for package {PackageNumber} — continuing without evidence image", measurement.PackageNumber);
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

        // MISMATCH: confirmed on real hardware (2026-09-02 field diagnosis)
        // that the TCP push's own embedded "I" frame image is a LOW-
        // RESOLUTION PREVIEW — 128x72px, ~16KB — not the device's actual
        // capture. The same device's HTTP /alibi/{packageNumber} endpoint
        // returns the true ~1280x720px, ~1MB capture for the identical
        // package. Previously this only called the HTTP fallback when the
        // TCP push carried NO image at all (imageHandle is null) — which
        // never happens in practice, since the device always pushes SOME
        // image, just the low-res one. That made every mismatch evidence
        // photo silently use the blurry preview instead of the real photo.
        //
        // Now: whenever evidence is required and a PackageNumber is known,
        // ALWAYS attempt the HTTP fetch and, if it succeeds, REPLACE
        // whatever TCP-push image we already have with the full-resolution
        // one (deleting the discarded low-res temp file). If the HTTP
        // fallback is unavailable (not configured, unreachable, or the
        // device doesn't answer), the low-res TCP image — if any — is kept
        // rather than losing evidence entirely; this still fails silently
        // (Unavailable), never blocking the mismatch result itself.
        _logger.LogInformation(
            "Mismatch evidence check for measurement {MeasurementId}: RequiresEvidence={RequiresEvidence}, hadImageFromDevicePush={HadDirectImage}, packageNumber='{PackageNumber}'",
            result.MeasurementId, result.RequiresEvidence, imageHandle is not null, submission.PackageNumber);

        if (result.RequiresEvidence && !string.IsNullOrWhiteSpace(submission.PackageNumber))
        {
            var highResHandle = await TryFetchEvidenceImageAsync(submission.PackageNumber, ct);
            if (highResHandle is not null)
            {
                if (imageHandle is not null) await _imageStore.DeleteAsync(imageHandle, ct);
                imageHandle = highResHandle;
            }
        }

        // Upload evidence once, confirm, then delete. If the upload itself
        // fails, the durable outbox owns the retry — this method does not
        // loop or block on it.
        var evidence = EvidenceOutcome.Unavailable;
        if (result.RequiresEvidence && imageHandle is not null)
        {
            var evidenceKey = _keyGenerator.NewKey();
            try
            {
                var bytes = await _imageStore.ReadAsync(imageHandle, ct);
                var mimeType = ImageFormatSniffer.Sniff(bytes) ?? "image/jpeg";
                var uploadResult = await _gbexClient.UploadEvidenceAsync(result.MeasurementId, bytes, mimeType, evidenceKey, ct);
                if (uploadResult is EvidenceUploadOutcome)
                {
                    await _imageStore.DeleteAsync(imageHandle, ct);
                    evidence = EvidenceOutcome.Uploaded;
                }
                else if (uploadResult is GbexApiResult.TransientFailure)
                {
                    await EnqueueEvidenceRetryAsync(submission.Barcode, result.MeasurementId, imageHandle, evidenceKey, ct);
                    evidence = EvidenceOutcome.QueuedForRetry;
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
                evidence = EvidenceOutcome.QueuedForRetry;
            }
        }

        SetState(AgentWorkflowState.OnHoldMismatch);
        return new MeasureOutcome.Mismatch(result.MeasurementId, evidence);
    }

    /// <summary>Best-effort fetch of a mismatch's evidence image via the optional HTTP fallback client — never throws, returns null on any failure (unreachable device, HTTP not configured, malformed response, or no image on that package number).</summary>
    private async Task<string?> TryFetchEvidenceImageAsync(string packageNumber, CancellationToken ct)
    {
        try
        {
            var capture = await _easyCubeClient.GetByPackageNumberAsync(packageNumber, ct);

            if (capture is not MeasurementOutcome outcome)
            {
                _logger.LogWarning("EasyCube /alibi/{PackageNumber} did not return a measurement: {ResultType}", packageNumber, capture.GetType().Name);
                return null;
            }

            if (string.IsNullOrEmpty(outcome.Measurement?.ImageBase64))
            {
                _logger.LogWarning("EasyCube /alibi/{PackageNumber} returned a measurement with no ImageBase64 field (null or empty)", packageNumber);
                return null;
            }

            _logger.LogInformation("EasyCube /alibi/{PackageNumber} returned ImageBase64 of length {Length}", packageNumber, outcome.Measurement.ImageBase64.Length);

            var bytes = EasyCubeImageDecoder.TryDecode(outcome.Measurement.ImageBase64);
            if (bytes is null)
            {
                _logger.LogWarning("EasyCube's /alibi response for package {PackageNumber} carried an undecodable image payload (first 40 chars: '{Prefix}')",
                    packageNumber, outcome.Measurement.ImageBase64[..Math.Min(40, outcome.Measurement.ImageBase64.Length)]);
                return null;
            }

            var mimeType = ImageFormatSniffer.Sniff(bytes);
            if (mimeType is null)
            {
                _logger.LogWarning("EasyCube /alibi/{PackageNumber} image decoded to {Bytes} bytes but the content is not a recognized image format (first bytes: {Prefix})",
                    packageNumber, bytes.Length, Convert.ToHexString(bytes[..Math.Min(8, bytes.Length)]));
                return null;
            }

            _logger.LogInformation("EasyCube /alibi/{PackageNumber} image decoded successfully, {Bytes} bytes, format {MimeType}", packageNumber, bytes.Length, mimeType);
            return await _imageStore.SaveTemporaryAsync(bytes, mimeType, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not fetch evidence image via HTTP fallback for package {PackageNumber}", packageNumber);
            return null;
        }
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
