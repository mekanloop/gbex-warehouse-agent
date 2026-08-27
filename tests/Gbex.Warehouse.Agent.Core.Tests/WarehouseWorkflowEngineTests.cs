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
            .ReturnsAsync(MeasurementOutcome.Ok(Measurement(imageBase64: Convert.ToBase64String(new byte[] { 1, 2, 3 }))));
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
            .ReturnsAsync(MeasurementOutcome.Ok(Measurement(imageBase64: Convert.ToBase64String(new byte[] { 1, 2, 3 }))));
        fx.ImageStore.Setup(s => s.SaveTemporaryAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("temp/handle.jpg");
        fx.ImageStore.Setup(s => s.ReadAsync("temp/handle.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3 });
        fx.GbexClient.Setup(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementSubmitOutcome.Ok(new MeasurementSubmissionResult { MeasurementId = "m2", Result = MeasurementResultKind.Mismatch, RequiresEvidence = true }));
        fx.GbexClient.Setup(c => c.UploadEvidenceAsync("m2", It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EvidenceUploadOutcome.Ok("https://example/photo.jpg"));

        var engine = fx.BuildEngine();
        var result = await engine.MeasureAndSubmitAsync(Order(), CancellationToken.None);

        var mismatch = Assert.IsType<MeasureOutcome.Mismatch>(result);
        Assert.True(mismatch.EvidenceUploaded);
        Assert.Equal(AgentWorkflowState.OnHoldMismatch, engine.State);
        fx.ImageStore.Verify(s => s.DeleteAsync("temp/handle.jpg", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MeasureAndSubmit_retains_the_image_in_the_outbox_when_evidence_upload_fails_transiently()
    {
        var fx = new Fixture();
        fx.EasyCubeClient.Setup(c => c.CaptureMeasurementAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementOutcome.Ok(Measurement(imageBase64: Convert.ToBase64String(new byte[] { 1, 2, 3 }))));
        fx.ImageStore.Setup(s => s.SaveTemporaryAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("temp/handle.jpg");
        fx.ImageStore.Setup(s => s.ReadAsync("temp/handle.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3 });
        fx.GbexClient.Setup(c => c.SubmitMeasurementAsync(It.IsAny<MeasurementSubmission>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MeasurementSubmitOutcome.Ok(new MeasurementSubmissionResult { MeasurementId = "m3", Result = MeasurementResultKind.Mismatch, RequiresEvidence = true }));
        fx.GbexClient.Setup(c => c.UploadEvidenceAsync("m3", It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GbexApiResult.TransientFailure("network"));

        var engine = fx.BuildEngine();
        var result = await engine.MeasureAndSubmitAsync(Order(), CancellationToken.None);

        var mismatch = Assert.IsType<MeasureOutcome.Mismatch>(result);
        Assert.False(mismatch.EvidenceUploaded);
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
}
