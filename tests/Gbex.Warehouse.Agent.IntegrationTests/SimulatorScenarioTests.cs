using Gbex.EasyCube.Simulator;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Correlation;
using Gbex.Warehouse.Agent.Infrastructure.EasyCube;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gbex.Warehouse.Agent.IntegrationTests;

/// <summary>Exercises every required simulator error condition against the REAL EasyCubeClient — the whole point of the simulator is that this needs no physical hardware.</summary>
public class SimulatorScenarioTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _http = null!;
    private EasyCubeClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        _http = _factory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(2); // short, so the "delayed response" scenario reliably times out in-test
        var options = Options.Create(new EasyCubeOptions { BaseUrl = "http://localhost", RequestTimeout = TimeSpan.FromSeconds(2) });
        _client = new EasyCubeClient(_http, options, NullLogger<EasyCubeClient>.Instance);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _factory.DisposeAsync();
    }

    private Task Configure(SimulatorScenario scenario) =>
        _http.PostAsJsonAsync("/simulator/configure", new { scenario, expectedBarcode = "GBEX2508230001" });

    [Fact]
    public async Task Healthy_device_reports_device_info_successfully()
    {
        await Configure(SimulatorScenario.Healthy);
        var result = await _client.GetDeviceInfoAsync(CancellationToken.None);
        Assert.IsType<DeviceHealth>(result);
    }

    [Fact]
    public async Task Unhealthy_device_surfaces_as_a_device_error_not_a_crash()
    {
        await Configure(SimulatorScenario.UnhealthyDevice);
        var result = await _client.GetDeviceInfoAsync(CancellationToken.None);
        Assert.IsType<EasyCubeResult.DeviceError>(result);
    }

    [Fact]
    public async Task Normal_measurement_captures_successfully()
    {
        await Configure(SimulatorScenario.NormalMeasurement);
        var result = await _client.CaptureMeasurementAsync(CancellationToken.None);
        var ok = Assert.IsType<MeasurementOutcome>(result);
        Assert.NotNull(ok.Measurement);
    }

    [Fact]
    public async Task Malformed_response_is_reported_without_throwing()
    {
        await Configure(SimulatorScenario.MalformedResponse);
        var result = await _client.CaptureMeasurementAsync(CancellationToken.None);
        Assert.IsType<EasyCubeResult.MalformedResponse>(result);
    }

    [Fact]
    public async Task Delayed_response_beyond_the_client_timeout_is_reported_as_a_timeout()
    {
        await _http.PostAsJsonAsync("/simulator/configure", new { scenario = SimulatorScenario.DelayedResponse, delayedResponseTimeMs = 10_000.0 });

        var result = await _client.CaptureMeasurementAsync(CancellationToken.None);
        Assert.IsType<EasyCubeResult.Timeout>(result);
    }

    [Fact]
    public async Task Stale_measurement_is_captured_but_rejected_by_correlation_validation()
    {
        await Configure(SimulatorScenario.StaleMeasurement);
        var result = await _client.CaptureMeasurementAsync(CancellationToken.None);
        var ok = Assert.IsType<MeasurementOutcome>(result);

        var validator = new MeasurementCorrelationValidator(new SystemClock(), TimeSpan.FromSeconds(30));
        var correlation = validator.Validate("GBEX2508230001", ok.Measurement!);

        Assert.IsType<CorrelationResult.StaleMeasurement>(correlation);
    }

    [Fact]
    public async Task Wrong_barcode_reported_by_the_device_is_rejected_by_correlation_validation()
    {
        await Configure(SimulatorScenario.WrongBarcode);
        var result = await _client.CaptureMeasurementAsync(CancellationToken.None);
        var ok = Assert.IsType<MeasurementOutcome>(result);

        var validator = new MeasurementCorrelationValidator(new SystemClock(), TimeSpan.FromMinutes(5));
        var correlation = validator.Validate("GBEX2508230001", ok.Measurement!);

        Assert.IsType<CorrelationResult.DeviceBarcodeMismatch>(correlation);
    }

    [Fact]
    public async Task Duplicate_package_number_on_a_second_capture_is_rejected_after_the_first_is_consumed()
    {
        await Configure(SimulatorScenario.DuplicatePackageNumber);
        var validator = new MeasurementCorrelationValidator(new SystemClock(), TimeSpan.FromMinutes(5));

        var first = Assert.IsType<MeasurementOutcome>(await _client.CaptureMeasurementAsync(CancellationToken.None));
        Assert.IsType<CorrelationResult.Valid>(validator.Validate("GBEX2508230001", first.Measurement!));
        validator.MarkConsumed(first.Measurement!.PackageNumber);

        var second = Assert.IsType<MeasurementOutcome>(await _client.CaptureMeasurementAsync(CancellationToken.None));
        var correlation = validator.Validate("GBEX2508230002", second.Measurement!);

        Assert.IsType<CorrelationResult.PackageNumberAlreadyUsed>(correlation);
    }

    [Fact]
    public async Task Device_error_response_surfaces_the_devices_own_error_message()
    {
        await Configure(SimulatorScenario.DeviceErrorResponse);
        var result = await _client.CaptureMeasurementAsync(CancellationToken.None);
        var error = Assert.IsType<EasyCubeResult.DeviceError>(result);
        Assert.False(string.IsNullOrEmpty(error.Message));
    }

    [Fact]
    public async Task Temporary_image_is_present_on_a_captured_measurement()
    {
        await Configure(SimulatorScenario.NormalMeasurement);
        var result = await _client.CaptureMeasurementAsync(CancellationToken.None);
        var ok = Assert.IsType<MeasurementOutcome>(result);
        Assert.False(string.IsNullOrEmpty(ok.Measurement!.ImageBase64));
    }
}
