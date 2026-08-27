using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gbex.EasyCube.Simulator;

/// <summary>
/// TEST TOOL ONLY — simulates the manufacturer's real "EasyCube TCP/IP
/// Protocol" server (the raw-socket "Protocol 0" surface, separate from the
/// HTTP Web API this same simulator process also exposes). Binds to an
/// OS-assigned ephemeral port (Port 0) so parallel test runs never collide;
/// tests read the actual bound port via GET /simulator/tcp-port. Accepts one
/// client connection at a time — the real device is documented as a single
/// TCP/IP server, and this Agent never expects more than one Windows PC per
/// EasyCube. Exposes raw send control via /simulator/tcp-push and
/// /simulator/tcp-push-raw so tests can script exact byte sequences
/// (fragmentation, multiple frames per packet, malformed frames).
/// </summary>
public sealed class EasyCubeTcpSimulator : IHostedService
{
    private readonly ScenarioState _state;
    private readonly ILogger<EasyCubeTcpSimulator> _logger;
    private TcpListener? _listener;
    private TcpClient? _currentClient;
    private CancellationTokenSource? _acceptLoopCts;

    public int Port { get; private set; }

    public EasyCubeTcpSimulator(ScenarioState state, ILogger<EasyCubeTcpSimulator> logger)
    {
        _state = state;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptLoopCts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_acceptLoopCts.Token);

        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _currentClient?.Dispose();
                _currentClient = client;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    /// <summary>Sends one manufacturer-format MFR frame for the current scenario state — the same shape as a real Data-Auto-Send push.</summary>
    public Task PushMeasurementAsync(bool fragmented)
    {
        var frame = BuildMfrFrame();
        return fragmented ? SendFragmentedAsync(frame) : SendRawAsync(frame);
    }

    public async Task SendRawAsync(string raw)
    {
        var client = await WaitForClientAsync();
        if (client is null)
        {
            _logger.LogWarning("tcp-push requested but no client connected within the wait window");
            return;
        }

        var bytes = Encoding.ASCII.GetBytes(raw);
        await client.GetStream().WriteAsync(bytes);
    }

    /// <summary>
    /// The accept loop's continuation (after AcceptTcpClientAsync completes)
    /// runs on the thread pool and is not guaranteed to have registered
    /// _currentClient by the moment a client-side ConnectAsync returns — a
    /// real race under test/CI scheduling, not a hypothetical. Pushing
    /// immediately after "the client is connected" is exactly the scenario
    /// tests exercise, so this waits briefly rather than failing that race.
    /// </summary>
    private async Task<TcpClient?> WaitForClientAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var client = _currentClient;
            if (client is not null && client.Connected) return client;
            await Task.Delay(10);
        }
        return null;
    }

    /// <summary>Writes the frame in two separate socket writes with a short delay between them — exercises the Agent's TCP fragmentation handling for real, not just in a unit test.</summary>
    private async Task SendFragmentedAsync(string raw)
    {
        var client = await WaitForClientAsync();
        if (client is null)
        {
            _logger.LogWarning("tcp-push (fragmented) requested but no client connected within the wait window");
            return;
        }

        var splitPoint = raw.Length / 2;
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(raw[..splitPoint]));
        await Task.Delay(50);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(raw[splitPoint..]));
    }

    /// <summary>
    /// All numeric formatting here MUST be invariant-culture — this bit a
    /// real integration test run under a tr-TR host locale: "0.0" formatted
    /// with the current culture renders as "40,0", and since the wire
    /// format is itself comma-delimited, that silently corrupts the frame
    /// into unparseable garbage (discarded by EasyCubeTcpListener as
    /// malformed, with no visible error — just a measurement that never
    /// arrives). The real device's firmware is fixed/invariant regardless
    /// of host locale, so the simulator must be too.
    /// </summary>
    private string BuildMfrFrame()
    {
        var packageNumber = _state.NewPackageNumber();
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return "{MFR,DSN," + _state.DeviceId +
               ",P," + packageNumber +
               ",T," + timestamp +
               ",L," + _state.LengthCm.ToString("0.0", CultureInfo.InvariantCulture) + ",LU,cm" +
               ",W," + _state.WidthCm.ToString("0.0", CultureInfo.InvariantCulture) + ",WU,cm" +
               ",H," + _state.HeightCm.ToString("0.0", CultureInfo.InvariantCulture) + ",HU,cm" +
               ",WT," + _state.WeightKg.ToString("0.000", CultureInfo.InvariantCulture) + ",WTU,kg" +
               ",B," + _state.ExpectedBarcode + "}";
    }

    /// <summary>Forcibly closes the current client connection — simulates a cable pull / device reboot so tests can verify the Agent's reconnect-with-backoff behavior for real.</summary>
    public void DropCurrentClient()
    {
        _currentClient?.Dispose();
        _currentClient = null;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _acceptLoopCts?.Cancel();
        _currentClient?.Dispose();
        _listener?.Stop();
        return Task.CompletedTask;
    }
}
