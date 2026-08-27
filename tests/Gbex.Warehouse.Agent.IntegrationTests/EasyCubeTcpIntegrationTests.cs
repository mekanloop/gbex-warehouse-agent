using Gbex.EasyCube.Simulator;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Correlation;
using Gbex.Warehouse.Agent.Core.Idempotency;
using Gbex.Warehouse.Agent.Core.Models;
using Gbex.Warehouse.Agent.Core.Workflow;
using Gbex.Warehouse.Agent.Infrastructure.EasyCube;
using Gbex.Warehouse.Agent.Infrastructure.Evidence;
using Gbex.Warehouse.Agent.Infrastructure.Gbex;
using Gbex.Warehouse.Agent.Infrastructure.Outbox;
using Gbex.Warehouse.Agent.Infrastructure.Secrets;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gbex.Warehouse.Agent.IntegrationTests;

/// <summary>
/// The PRIMARY flow, exercised end-to-end over a REAL TCP socket: a real
/// EasyCubeTcpListener connects to a real raw-socket server (the EasyCube
/// simulator's new TCP mode — see EasyCubeTcpSimulator), receives an
/// unsolicited pushed measurement in the manufacturer's real wire format,
/// and drives a real WarehouseWorkflowEngine against a real fake GBEX
/// backend. No mocks below the two test servers themselves.
/// </summary>
public class EasyCubeTcpIntegrationTests : IAsyncLifetime
{
    private FakeGbexBackend _gbexBackend = null!;
    private WebApplicationFactory<Program> _easyCubeFactory = null!;
    private HttpClient _simulatorControlHttp = null!;
    private string _tempDir = null!;
    private int _tcpPort;

    public async Task InitializeAsync()
    {
        _gbexBackend = new FakeGbexBackend();
        await _gbexBackend.StartAsync();

        _easyCubeFactory = new WebApplicationFactory<Program>();
        _simulatorControlHttp = _easyCubeFactory.CreateClient();

        _tempDir = Path.Combine(Path.GetTempPath(), "gbex-agent-tcp-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var portResponse = await _simulatorControlHttp.GetFromJsonAsync<PortResponse>("/simulator/tcp-port");
        _tcpPort = portResponse!.Port;
    }

    public async Task DisposeAsync()
    {
        await _gbexBackend.DisposeAsync();
        _simulatorControlHttp.Dispose();
        await _easyCubeFactory.DisposeAsync();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private sealed record PortResponse(int Port);

    private async Task ConfigureSimulatorAsync(string expectedBarcode, double weightKg = 5)
    {
        await _simulatorControlHttp.PostAsJsonAsync("/simulator/configure", new
        {
            scenario = SimulatorScenario.NormalMeasurement,
            expectedBarcode,
            weightKg,
            lengthCm = 40.0,
            widthCm = 30.0,
            heightCm = 20.0,
        });
    }

    private (WarehouseWorkflowEngine Engine, EasyCubeTcpListener Listener) BuildEngineAndListener()
    {
        var secretStore = new InMemorySecretStore();
        secretStore.SaveStationSecretAsync(_gbexBackend.ValidToken, CancellationToken.None).GetAwaiter().GetResult();

        var gbexOptions = Options.Create(new GbexApiOptions { BaseUrl = _gbexBackend.BaseUrl, AllowInsecureForDevelopment = true });
        var gbexClient = new GbexApiClient(new HttpClient(), gbexOptions, secretStore, NullLogger<GbexApiClient>.Instance);

        // Not exercised by these tests (the TCP path never calls it), but
        // required by the engine's constructor — same as production, where
        // it's the optional fallback.
        var easyCubeHttpOptions = Options.Create(new EasyCubeOptions { BaseUrl = "http://localhost" });
        var easyCubeHttpClient = new EasyCubeClient(_easyCubeFactory.CreateClient(), easyCubeHttpOptions, NullLogger<EasyCubeClient>.Instance);

        var imageStore = new TemporaryImageStore(Path.Combine(_tempDir, "images"), NullLogger<TemporaryImageStore>.Instance);
        var outboxStore = new SqliteOutboxStore(Path.Combine(_tempDir, "outbox.db"), new SystemClock(), NullLogger<SqliteOutboxStore>.Instance);

        var engine = new WarehouseWorkflowEngine(
            gbexClient,
            easyCubeHttpClient,
            imageStore,
            outboxStore,
            new GuidIdempotencyKeyGenerator(),
            new MeasurementCorrelationValidator(new SystemClock(), TimeSpan.FromMinutes(5)),
            NullLogger<WarehouseWorkflowEngine>.Instance);

        var tcpOptions = Options.Create(new EasyCubeTcpOptions
        {
            Host = "127.0.0.1",
            Port = _tcpPort,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            InitialBackoff = TimeSpan.FromMilliseconds(100),
            MaxBackoff = TimeSpan.FromSeconds(1),
        });
        var listener = new EasyCubeTcpListener(tcpOptions, NullLogger<EasyCubeTcpListener>.Instance);

        return (engine, listener);
    }

    private static async Task WaitForStateAsync(EasyCubeTcpListener listener, EasyCubeConnectionState expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (listener.State != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.Equal(expected, listener.State);
    }

    [Fact]
    public async Task Pushed_measurement_is_looked_up_correlated_and_submitted_automatically()
    {
        const string barcode = "GBEX2508230001";
        _gbexBackend.ExpectedBarcode = barcode;
        _gbexBackend.NextResult = "pass";
        await ConfigureSimulatorAsync(barcode);

        var (engine, listener) = BuildEngineAndListener();
        var received = new TaskCompletionSource<DeviceMeasurementResult>();
        listener.MeasurementReceived += async (measurement, ct) =>
        {
            received.TrySetResult(await engine.HandleDeviceMeasurementAsync(measurement, ct));
        };

        using var cts = new CancellationTokenSource();
        _ = listener.StartAsync(cts.Token);
        try
        {
            await WaitForStateAsync(listener, EasyCubeConnectionState.Connected, TimeSpan.FromSeconds(5));

            await _simulatorControlHttp.PostAsJsonAsync("/simulator/tcp-push", new TcpPushBody());

            var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(result.Order);
            Assert.Equal(barcode, result.Order!.GbexBarcode);
            Assert.IsType<MeasureOutcome.Pass>(result.Outcome);
            Assert.Equal(1, _gbexBackend.MeasurementSubmitCallCount);
        }
        finally
        {
            cts.Cancel();
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_frame_split_across_two_socket_writes_is_still_processed()
    {
        const string barcode = "GBEX2508230002";
        _gbexBackend.ExpectedBarcode = barcode;
        _gbexBackend.NextResult = "pass";
        await ConfigureSimulatorAsync(barcode);

        var (engine, listener) = BuildEngineAndListener();
        var received = new TaskCompletionSource<DeviceMeasurementResult>();
        listener.MeasurementReceived += async (measurement, ct) =>
        {
            received.TrySetResult(await engine.HandleDeviceMeasurementAsync(measurement, ct));
        };

        using var cts = new CancellationTokenSource();
        _ = listener.StartAsync(cts.Token);
        try
        {
            await WaitForStateAsync(listener, EasyCubeConnectionState.Connected, TimeSpan.FromSeconds(5));

            await _simulatorControlHttp.PostAsJsonAsync("/simulator/tcp-push", new TcpPushBody(Fragmented: true));

            var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsType<MeasureOutcome.Pass>(result.Outcome);
        }
        finally
        {
            cts.Cancel();
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Two_frames_concatenated_in_one_packet_are_both_processed()
    {
        // This test only asserts that BOTH frames reach the workflow engine
        // (the framing behavior under test) — not that both lookups
        // succeed. FakeGbexBackend only recognizes one ExpectedBarcode at a
        // time, so barcodeB's lookup will fail with NotFound; that's fine,
        // HandleDeviceMeasurementAsync still returns a result for it either way.
        const string barcodeA = "GBEX2508230003";
        const string barcodeB = "GBEX2508230004";
        _gbexBackend.ExpectedBarcode = barcodeA;
        await ConfigureSimulatorAsync(barcodeA);

        var (engine, listener) = BuildEngineAndListener();
        var receivedCount = 0;
        var both = new TaskCompletionSource();
        listener.MeasurementReceived += async (measurement, ct) =>
        {
            await engine.HandleDeviceMeasurementAsync(measurement, ct);
            if (Interlocked.Increment(ref receivedCount) >= 2) both.TrySetResult();
        };

        using var cts = new CancellationTokenSource();
        _ = listener.StartAsync(cts.Token);
        try
        {
            await WaitForStateAsync(listener, EasyCubeConnectionState.Connected, TimeSpan.FromSeconds(5));

            // Two complete MFR frames written as a single raw payload — no
            // separator beyond their own matching braces, exactly as the
            // device could deliver if it queued two measurements before the
            // Agent's next socket read.
            string Frame(string packageNumber, string barcode) =>
                "{MFR,DSN,00000000,P," + packageNumber + ",T," + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                ",L,40.0,LU,cm,W,30.0,WU,cm,H,20.0,HU,cm,WT,5.000,WTU,kg,B," + barcode + "}";

            var raw = Frame("9001", barcodeA) + Frame("9002", barcodeB);
            await _simulatorControlHttp.PostAsJsonAsync("/simulator/tcp-push-raw", new TcpRawBody(raw));

            await both.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, receivedCount);
        }
        finally
        {
            cts.Cancel();
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_malformed_frame_is_discarded_without_breaking_the_next_real_measurement()
    {
        const string barcode = "GBEX2508230005";
        _gbexBackend.ExpectedBarcode = barcode;
        _gbexBackend.NextResult = "pass";
        await ConfigureSimulatorAsync(barcode);

        var (engine, listener) = BuildEngineAndListener();
        var received = new TaskCompletionSource<DeviceMeasurementResult>();
        listener.MeasurementReceived += async (measurement, ct) =>
        {
            received.TrySetResult(await engine.HandleDeviceMeasurementAsync(measurement, ct));
        };

        using var cts = new CancellationTokenSource();
        _ = listener.StartAsync(cts.Token);
        try
        {
            await WaitForStateAsync(listener, EasyCubeConnectionState.Connected, TimeSpan.FromSeconds(5));

            await _simulatorControlHttp.PostAsJsonAsync("/simulator/tcp-push-raw", new TcpRawBody("{GARBAGE,not,a,real,frame}"));
            await _simulatorControlHttp.PostAsJsonAsync("/simulator/tcp-push", new TcpPushBody());

            var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsType<MeasureOutcome.Pass>(result.Outcome);
        }
        finally
        {
            cts.Cancel();
            await listener.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Reconnects_with_backoff_after_the_connection_is_dropped_and_keeps_working()
    {
        const string barcode = "GBEX2508230006";
        _gbexBackend.ExpectedBarcode = barcode;
        _gbexBackend.NextResult = "pass";
        await ConfigureSimulatorAsync(barcode);

        var (engine, listener) = BuildEngineAndListener();
        var received = new TaskCompletionSource<DeviceMeasurementResult>();
        listener.MeasurementReceived += async (measurement, ct) =>
        {
            received.TrySetResult(await engine.HandleDeviceMeasurementAsync(measurement, ct));
        };

        using var cts = new CancellationTokenSource();
        _ = listener.StartAsync(cts.Token);
        try
        {
            await WaitForStateAsync(listener, EasyCubeConnectionState.Connected, TimeSpan.FromSeconds(5));

            await _simulatorControlHttp.PostAsJsonAsync("/simulator/tcp-drop", new { });

            // Drops immediately show as Reconnecting, then Connected again
            // once the backoff loop reopens the socket.
            await WaitForStateAsync(listener, EasyCubeConnectionState.Connected, TimeSpan.FromSeconds(5));

            await _simulatorControlHttp.PostAsJsonAsync("/simulator/tcp-push", new TcpPushBody());
            var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsType<MeasureOutcome.Pass>(result.Outcome);
        }
        finally
        {
            cts.Cancel();
            await listener.StopAsync(CancellationToken.None);
        }
    }

    private sealed record TcpPushBody(bool? Fragmented = null);
    private sealed record TcpRawBody(string Raw);
}
