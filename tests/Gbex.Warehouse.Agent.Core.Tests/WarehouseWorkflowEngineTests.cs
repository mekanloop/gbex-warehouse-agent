using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Correlation;
using Gbex.Warehouse.Agent.Core.Idempotency;
using Gbex.Warehouse.Agent.Core.Models;
using Gbex.Warehouse.Agent.Core.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

public class WarehouseWorkflowEngineTests
{
    // Minimal real JPEG signature (SOI marker) — ImageFormatSniffer now
    // rejects arbitrary bytes that don't match a recognized image format
    // (see TemporaryImageStore/ImageFormatSniffer), so fake evidence bytes
    // in these tests must actually look like an image.
    private static readonly byte[] FakeJpegBytes = { 0xFF, 0xD8, 0xFF };
    // PNG signature — a real EasyCube unit (2026-08-27) was found to send
    // PNG image data despite the Agent always declaring "image/jpeg", which
    // silently lost every mismatch's evidence photo. The mimeType passed to
    // the image store and to the GBEX upload must be sniffed from the real
    // bytes, not assumed.
    private static readonly byte[] FakePngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Always returns the REAL current time — these tests aren't exercising
    /// staleness rejection (MeasurementCorrelationValidatorTests owns that),
    /// so the validator's clock must track wall-clock time the same way
    /// Measurement()'s own DateTimeOffset.UtcNow does. A clock frozen at
    /// construction would drift "into the future" relative to a measurement
    /// timestamped moments later in the same test, incorrectly triggering
    /// StaleMeasurement (age &lt; 0).
    /// </summary>
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private static StationOrderDto Order() => new()
    {
        Id = "order_1",
        GbexBarcode = "GBEX2508230001",
        Status = "on_hold",
        DestinationCountry = "DE",
        DestinationCity = "Berlin",
        DeclaredWeight = 5,
        DeclaredDesi = 5,
        DeclaredLength = 40,
        DeclaredWidth = 30,
        DeclaredHeight = 20,
        FulfillmentMode = "live_carrier",
        RequiresManualCarrierLabel = false,
    };

    private static CapturedMeasurement Measurement(string? imageBase64 = null) => new()
    {
        DeviceId = "00000000",
        PackageNumber = "419",
        Timestamp = DateTimeOffset.UtcNow,
        WeightKg = 5,
        LengthCm = 40,
        WidthCm = 30,
        HeightCm = 20,
        ImageBase64 = imageBase64,
    };

    private sealed class Fixture
    {
        public Mock<IGbexApiClient> GbexClient { get; } = new();
        public Mock<IEasyCubeClient> EasyCubeClient { get; } = new();
        public Mock<IEvidenceImageStore> ImageStore { get; } = new();
        public Mock<IOutboxStore> Outbox { get; } = new();
        public FakeClock Clock { get; } = new();

        public WarehouseWorkflowEngine BuildEngine() => new(
            GbexClient.Object,
            EasyCubeClient.Object,
            ImageStore.Object,
            Outbox.Object,
            new GuidIdempotencyKeyGenerator(),
            new MeasurementCorrelationValidator(Clock),
            NullLogger<WarehouseWorkflowEngine>.Instance);
    }

    [Fact]
    public async Task ScanAndLookup_returns_the_order_on_success()
    {
        var fx = new Fixture();
        fx.GbexClient.Setup(c => c.LookupOrderAsync("GBEX2508230001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrderLookupOutcome.Ok(Order()));

        var engine = fx.BuildEngine();
        var result = await engine.ScanAndLookupAsync("GBEX2508230001", CancellationToken.None);

        var found = Assert.IsType<LookupOutcome.Found>(result);
        Assert.Equal("GBEX2508230001", found.Order.GbexBarcode);
        Assert.Equal(AgentWorkflowState.OrderFound, engine.State);
    }

    [Fact]
    public async Task ScanAndLookup_rejects_an_invalid_barcode_without_calling_gbex()
    {
        var fx = new Fixture();
        var engine = fx.BuildEngine();

        var result = await engine.ScanAndLookupAsync("not-a-barcode", CancellationToken.None);

        Assert.IsType<LookupOutcome.InvalidBarcode>(result);
        fx.GbexClient.Verify(c => c.LookupOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScanAndLookup_surfaces_unauthorized_state()
    {
        var fx = new Fixture();
        fx.GbexClient.Setup(c => c.LookupOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GbexApiResult.Unauthorized());

        var engine = fx.BuildEngine();
        await engine.ScanAndLookupAsync("GBEX2508230001", CancellationToken.None);

        Assert.Equal(AgentWorkflowState.StationUnauthorized, engine.State);
    }

    [Fact]
    public async Task MeasureAndSubmit_on_PASS_deletes_the_temporary_image_immediately()
    {
        var fx = new Fixture();
        fx.EasyCubeClient.Setup(c => c.CaptureMeasurementAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementOutcome.Ok(Measurement(imageBase64: Convert.ToBase64String(FakeJpegBytes))));
        fx.ImageStore.Setup(s => s.SaveTemporaryAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("temp/handle.jpg");
        fx.GbexClient.Setup(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementSubmitOutcome.Ok(new MeasurementSubmissionResult { MeasurementId = "m1", Result = MeasurementResultKind.Pass, RequiresEvidence = false }));

        var engine = fx.BuildEngine();
        var result = await engine.MeasureAndSubmitAsync(Order(), CancellationToken.None);

        Assert.IsType<MeasureOutcome.Pass>(result);
        Assert.Equal(AgentWorkflowState.VerifiedPass, engine.State);
        fx.ImageStore.Verify(s => s.DeleteAsync("temp/handle.jpg", It.IsAny<CancellationToken>()), Times.Once);
        fx.GbexClient.Verify(c => c.UploadEvidenceAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MeasureAndSubmit_on_MISMATCH_uploads_evidence_then_deletes_the_local_image()
    {
        var fx = new Fixture();
        fx.EasyCubeClient.Setup(c => c.CaptureMeasurementAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementOutcome.Ok(Measurement(imageBase64: Convert.ToBase64String(FakeJpegBytes))));
        fx.ImageStore.Setup(s => s.SaveTemporaryAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("temp/handle.jpg");
        fx.ImageStore.Setup(s => s.ReadAsync("temp/handle.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FakeJpegBytes);
        fx.GbexClient.Setup(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementSubmitOutcome.Ok(new MeasurementSubmissionResult { MeasurementId = "m2", Result = MeasurementResultKind.Mismatch, RequiresEvidence = true }));
        fx.GbexClient.Setup(c => c.UploadEvidenceAsync("m2", It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EvidenceUploadOutcome.Ok("https://example/photo.jpg"));

        var engine = fx.BuildEngine();
        var result = await engine.MeasureAndSubmitAsync(Order(), CancellationToken.None);

        var mismatch = Assert.IsType<MeasureOutcome.Mismatch>(result);
        Assert.Equal(EvidenceOutcome.Uploaded, mismatch.Evidence);
        Assert.Equal(AgentWorkflowState.OnHoldMismatch, engine.State);
        fx.ImageStore.Verify(s => s.DeleteAsync("temp/handle.jpg", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MeasureAndSubmit_retains_the_image_in_the_outbox_when_evidence_upload_fails_transiently()
    {
        var fx = new Fixture();
        fx.EasyCubeClient.Setup(c => c.CaptureMeasurementAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementOutcome.Ok(Measurement(imageBase64: Convert.ToBase64String(FakeJpegBytes))));
        fx.ImageStore.Setup(s => s.SaveTemporaryAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("temp/handle.jpg");
        fx.ImageStore.Setup(s => s.ReadAsync("temp/handle.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FakeJpegBytes);
        fx.GbexClient.Setup(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementSubmitOutcome.Ok(new MeasurementSubmissionResult { MeasurementId = "m3", Result = MeasurementResultKind.Mismatch, RequiresEvidence = true }));
        fx.GbexClient.Setup(c => c.UploadEvidenceAsync("m3", It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GbexApiResult.TransientFailure("network"));

        var engine = fx.BuildEngine();
        var result = await engine.MeasureAndSubmitAsync(Order(), CancellationToken.None);

        var mismatch = Assert.IsType<MeasureOutcome.Mismatch>(result);
        Assert.Equal(EvidenceOutcome.QueuedForRetry, mismatch.Evidence);
        // NOT deleted — the durable outbox owns it now.
        fx.ImageStore.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        fx.Outbox.Verify(o => o.EnqueueAsync(It.Is<NewOutboxItem>(i => i.OperationType == OutboxOperationType.UploadEvidence && i.EvidenceFilePath == "temp/handle.jpg"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MeasureAndSubmit_queues_offline_on_transient_gbex_failure_and_never_calls_wallet_or_carrier_apis()
    {
        var fx = new Fixture();
        fx.EasyCubeClient.Setup(c => c.CaptureMeasurementAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementOutcome.Ok(Measurement()));
        fx.GbexClient.Setup(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GbexApiResult.TransientFailure("network"));

        var engine = fx.BuildEngine();
        var result = await engine.MeasureAndSubmitAsync(Order(), CancellationToken.None);

        Assert.IsType<MeasureOutcome.QueuedOffline>(result);
        Assert.Equal(AgentWorkflowState.OfflineQueued, engine.State);
        fx.Outbox.Verify(o => o.EnqueueAsync(It.Is<NewOutboxItem>(i => i.OperationType == OutboxOperationType.SubmitMeasurement), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MeasureAndSubmit_rejects_a_correlation_mismatch_before_ever_submitting()
    {
        var fx = new Fixture();
        fx.EasyCubeClient.Setup(c => c.CaptureMeasurementAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementOutcome.Ok(Measurement() with { DeviceReportedBarcode = "GBEX0000000000" }));

        var engine = fx.BuildEngine();
        var result = await engine.MeasureAndSubmitAsync(Order(), CancellationToken.None);

        Assert.IsType<MeasureOutcome.CorrelationRejected>(result);
        fx.GbexClient.Verify(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MeasureAndSubmit_surfaces_an_easycube_device_failure_without_crashing()
    {
        var fx = new Fixture();
        fx.EasyCubeClient.Setup(c => c.CaptureMeasurementAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EasyCubeResult.DeviceError("12", "camera disconnected error!"));

        var engine = fx.BuildEngine();
        var result = await engine.MeasureAndSubmitAsync(Order(), CancellationToken.None);

        var failure = Assert.IsType<MeasureOutcome.EasyCubeFailure>(result);
        Assert.Contains("camera disconnected", failure.Reason);
        Assert.Equal(AgentWorkflowState.EasyCubeError, engine.State);
    }

    // --- HandleDeviceMeasurementAsync: the PRIMARY TCP-push flow — the
    // barcode comes FROM the device's own pushed record, there is no
    // separate "operator scanned this on the PC" step. ---

    [Fact]
    public async Task HandleDeviceMeasurement_rejects_a_record_with_no_barcode_before_any_lookup()
    {
        var fx = new Fixture();
        var engine = fx.BuildEngine();

        var result = await engine.HandleDeviceMeasurementAsync(Measurement() with { DeviceReportedBarcode = null }, CancellationToken.None);

        Assert.Null(result.Order);
        Assert.IsType<MeasureOutcome.EasyCubeFailure>(result.Outcome);
        fx.GbexClient.Verify(c => c.LookupOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleDeviceMeasurement_rejects_a_record_whose_barcode_is_not_a_valid_gbex_shape()
    {
        var fx = new Fixture();
        var engine = fx.BuildEngine();

        var result = await engine.HandleDeviceMeasurementAsync(Measurement() with { DeviceReportedBarcode = "not-a-barcode" }, CancellationToken.None);

        Assert.Null(result.Order);
        Assert.IsType<MeasureOutcome.EasyCubeFailure>(result.Outcome);
        fx.GbexClient.Verify(c => c.LookupOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleDeviceMeasurement_surfaces_a_lookup_failure_without_a_resolved_order()
    {
        var fx = new Fixture();
        fx.GbexClient.Setup(c => c.LookupOrderAsync("GBEX2508230001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GbexApiResult.NotFound("bulunamadı"));

        var engine = fx.BuildEngine();
        var result = await engine.HandleDeviceMeasurementAsync(Measurement() with { DeviceReportedBarcode = "GBEX2508230001" }, CancellationToken.None);

        Assert.Null(result.Order);
        var lookupFailed = Assert.IsType<MeasureOutcome.LookupFailed>(result.Outcome);
        Assert.IsType<LookupOutcome.NotFound>(lookupFailed.Reason);
    }

    [Fact]
    public async Task HandleDeviceMeasurement_looks_up_correlates_and_submits_in_one_pass_on_success()
    {
        var fx = new Fixture();
        fx.GbexClient.Setup(c => c.LookupOrderAsync("GBEX2508230001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrderLookupOutcome.Ok(Order()));
        fx.GbexClient.Setup(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementSubmitOutcome.Ok(new MeasurementSubmissionResult { MeasurementId = "m-device-1", Result = MeasurementResultKind.Pass, RequiresEvidence = false }));

        var engine = fx.BuildEngine();
        var measurement = Measurement() with { DeviceReportedBarcode = "GBEX2508230001" };
        var result = await engine.HandleDeviceMeasurementAsync(measurement, CancellationToken.None);

        Assert.NotNull(result.Order);
        Assert.Equal("GBEX2508230001", result.Order!.GbexBarcode);
        var pass = Assert.IsType<MeasureOutcome.Pass>(result.Outcome);
        Assert.Equal("m-device-1", pass.MeasurementId);
        Assert.Equal(AgentWorkflowState.VerifiedPass, engine.State);

        // The device's own barcode is what's used for lookup — no separate
        // "operator scanned this on the PC" value ever enters this flow.
        fx.EasyCubeClient.Verify(c => c.CaptureMeasurementAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleDeviceMeasurement_rejects_a_second_push_reusing_the_same_package_number()
    {
        var fx = new Fixture();
        fx.GbexClient.Setup(c => c.LookupOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrderLookupOutcome.Ok(Order()));
        fx.GbexClient.Setup(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementSubmitOutcome.Ok(new MeasurementSubmissionResult { MeasurementId = "m-dup-1", Result = MeasurementResultKind.Pass, RequiresEvidence = false }));

        var engine = fx.BuildEngine();
        var measurement = Measurement() with { DeviceReportedBarcode = "GBEX2508230001" };

        var first = await engine.HandleDeviceMeasurementAsync(measurement, CancellationToken.None);
        Assert.IsType<MeasureOutcome.Pass>(first.Outcome);

        // Same PackageNumber pushed again (e.g. device retransmit after a
        // reconnect) — must be rejected, never resubmitted.
        var second = await engine.HandleDeviceMeasurementAsync(measurement, CancellationToken.None);
        var rejected = Assert.IsType<MeasureOutcome.CorrelationRejected>(second.Outcome);
        Assert.IsType<CorrelationResult.PackageNumberAlreadyUsed>(rejected.Reason);
        fx.GbexClient.Verify(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Mismatch evidence photo for the device-push flow: the TCP protocol
    // carries no image at all, so a mismatch's evidence photo (when
    // required) must come from an opportunistic fetch via the OPTIONAL HTTP
    // fallback client, correlated by PackageNumber. ---

    [Fact]
    public async Task HandleDeviceMeasurement_mismatch_fetches_evidence_via_http_fallback_when_the_push_carried_no_image()
    {
        var fx = new Fixture();
        fx.GbexClient.Setup(c => c.LookupOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrderLookupOutcome.Ok(Order()));
        fx.GbexClient.Setup(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementSubmitOutcome.Ok(new MeasurementSubmissionResult { MeasurementId = "m-photo-1", Result = MeasurementResultKind.Mismatch, RequiresEvidence = true }));
        // The device-push measurement itself has NO image (TCP protocol
        // limitation) — the fallback client is what supplies one, keyed by
        // the same PackageNumber the push reported.
        fx.EasyCubeClient.Setup(c => c.GetByPackageNumberAsync("419", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementOutcome.Ok(Measurement(imageBase64: Convert.ToBase64String(FakeJpegBytes))));
        fx.ImageStore.Setup(s => s.SaveTemporaryAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("temp/fallback-fetched.jpg");
        fx.ImageStore.Setup(s => s.ReadAsync("temp/fallback-fetched.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FakeJpegBytes);
        fx.GbexClient.Setup(c => c.UploadEvidenceAsync("m-photo-1", It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EvidenceUploadOutcome.Ok("https://example/photo.jpg"));

        var engine = fx.BuildEngine();
        var measurement = Measurement() with { DeviceReportedBarcode = "GBEX2508230001" }; // no ImageBase64
        var result = await engine.HandleDeviceMeasurementAsync(measurement, CancellationToken.None);

        var mismatch = Assert.IsType<MeasureOutcome.Mismatch>(result.Outcome);
        Assert.Equal(EvidenceOutcome.Uploaded, mismatch.Evidence);
        fx.EasyCubeClient.Verify(c => c.GetByPackageNumberAsync("419", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleDeviceMeasurement_mismatch_replaces_a_low_res_tcp_push_image_with_the_full_resolution_http_fetch()
    {
        // Regression test for a confirmed real-hardware bug (2026-09-02):
        // the TCP push's own embedded "I" frame image is a low-resolution
        // preview (128x72px on the real device, vs ~1280x720px from the
        // same device's HTTP /alibi endpoint for the identical package).
        // The engine must not skip the HTTP fetch just because SOME image
        // already came through the TCP push — it must always prefer the
        // full-resolution HTTP one when available, and discard the low-res
        // TCP image rather than uploading it.
        var fx = new Fixture();
        fx.GbexClient.Setup(c => c.LookupOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrderLookupOutcome.Ok(Order()));
        fx.GbexClient.Setup(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementSubmitOutcome.Ok(new MeasurementSubmissionResult { MeasurementId = "m-lowres-1", Result = MeasurementResultKind.Mismatch, RequiresEvidence = true }));

        // The TCP push itself DID carry an image (the low-res preview),
        // saved first; the HTTP fallback's fetch saves a SECOND, separate
        // temp file for the full-resolution replacement — SetupSequence
        // distinguishes the two calls so the test can assert the first
        // (low-res) one is discarded, not the second (high-res) one.
        fx.ImageStore.SetupSequence(s => s.SaveTemporaryAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("temp/tcp-lowres.jpg")
            .ReturnsAsync("temp/http-highres.jpg");
        // The HTTP fallback, keyed by the same PackageNumber, supplies the
        // real full-resolution capture.
        fx.EasyCubeClient.Setup(c => c.GetByPackageNumberAsync("419", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementOutcome.Ok(Measurement(imageBase64: Convert.ToBase64String(FakeJpegBytes))));
        fx.ImageStore.Setup(s => s.ReadAsync("temp/http-highres.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FakeJpegBytes);
        fx.GbexClient.Setup(c => c.UploadEvidenceAsync("m-lowres-1", It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EvidenceUploadOutcome.Ok("https://example/photo.jpg"));

        var engine = fx.BuildEngine();
        // The device-push measurement HAS an image (unlike the "no image"
        // test above) — this is the realistic real-hardware case.
        var measurement = Measurement(imageBase64: Convert.ToBase64String(FakeJpegBytes)) with { DeviceReportedBarcode = "GBEX2508230001" };
        var result = await engine.HandleDeviceMeasurementAsync(measurement, CancellationToken.None);

        var mismatch = Assert.IsType<MeasureOutcome.Mismatch>(result.Outcome);
        Assert.Equal(EvidenceOutcome.Uploaded, mismatch.Evidence);
        // The HTTP fallback must always be attempted, even though the TCP
        // push already provided an image — this is the actual fix.
        fx.EasyCubeClient.Verify(c => c.GetByPackageNumberAsync("419", It.IsAny<CancellationToken>()), Times.Once);
        // The low-res TCP image must be discarded, not uploaded.
        fx.ImageStore.Verify(s => s.DeleteAsync("temp/tcp-lowres.jpg", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleDeviceMeasurement_mismatch_uploads_a_PNG_evidence_photo_with_the_correct_sniffed_mime_type()
    {
        var fx = new Fixture();
        fx.GbexClient.Setup(c => c.LookupOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrderLookupOutcome.Ok(Order()));
        fx.GbexClient.Setup(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementSubmitOutcome.Ok(new MeasurementSubmissionResult { MeasurementId = "m-photo-png", Result = MeasurementResultKind.Mismatch, RequiresEvidence = true }));
        fx.EasyCubeClient.Setup(c => c.GetByPackageNumberAsync("419", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementOutcome.Ok(Measurement(imageBase64: Convert.ToBase64String(FakePngBytes))));
        fx.ImageStore.Setup(s => s.SaveTemporaryAsync(It.IsAny<byte[]>(), "image/png", It.IsAny<CancellationToken>()))
            .ReturnsAsync("temp/fallback-fetched.png");
        fx.ImageStore.Setup(s => s.ReadAsync("temp/fallback-fetched.png", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FakePngBytes);
        fx.GbexClient.Setup(c => c.UploadEvidenceAsync("m-photo-png", It.IsAny<byte[]>(), "image/png", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EvidenceUploadOutcome.Ok("https://example/photo.png"));

        var engine = fx.BuildEngine();
        var measurement = Measurement() with { DeviceReportedBarcode = "GBEX2508230001" };
        var result = await engine.HandleDeviceMeasurementAsync(measurement, CancellationToken.None);

        var mismatch = Assert.IsType<MeasureOutcome.Mismatch>(result.Outcome);
        Assert.Equal(EvidenceOutcome.Uploaded, mismatch.Evidence);
        fx.GbexClient.Verify(c => c.UploadEvidenceAsync("m-photo-png", It.IsAny<byte[]>(), "image/png", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleDeviceMeasurement_mismatch_reports_evidence_unavailable_when_the_http_fallback_cannot_supply_a_photo()
    {
        var fx = new Fixture();
        fx.GbexClient.Setup(c => c.LookupOrderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrderLookupOutcome.Ok(Order()));
        fx.GbexClient.Setup(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementSubmitOutcome.Ok(new MeasurementSubmissionResult { MeasurementId = "m-photo-2", Result = MeasurementResultKind.Mismatch, RequiresEvidence = true }));
        // HTTP fallback not configured/reachable — the common case when only
        // the TCP link is set up.
        fx.EasyCubeClient.Setup(c => c.GetByPackageNumberAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EasyCubeResult.Unreachable("ConnectFailure"));

        var engine = fx.BuildEngine();
        var measurement = Measurement() with { DeviceReportedBarcode = "GBEX2508230001" };
        var result = await engine.HandleDeviceMeasurementAsync(measurement, CancellationToken.None);

        var mismatch = Assert.IsType<MeasureOutcome.Mismatch>(result.Outcome);
        Assert.Equal(EvidenceOutcome.Unavailable, mismatch.Evidence);
        // No image was ever obtained — nothing to enqueue a retry for.
        fx.Outbox.Verify(o => o.EnqueueAsync(It.Is<NewOutboxItem>(i => i.OperationType == OutboxOperationType.UploadEvidence), It.IsAny<CancellationToken>()), Times.Never);
    }
}
