using System.Net.Sockets;
using System.Text;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.EasyCube;
using Gbex.Warehouse.Agent.Core.Models;
using Gbex.Warehouse.Agent.Core.Units;
using Gbex.Warehouse.Agent.Infrastructure.Retry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gbex.Warehouse.Agent.Infrastructure.EasyCube;

/// <summary>
/// The Agent's PRIMARY connection to EasyCube: a persistent raw TCP/IP
/// socket (Ethernet), speaking the manufacturer's own "Protocol 0" wire
/// format (see EasyCubeProtocolZeroParser) rather than the HTTP Web API
/// (EasyCubeClient — kept only as the optional manual fallback). The device
/// has its own USB-attached barcode scanner; every time it reads a barcode
/// and takes a measurement, it pushes one combined record over this socket,
/// unsolicited — this class never sends a command to request one.
///
/// Runs as a BackgroundService for the Agent's whole lifetime: connects,
/// reads and dispatches frames until the link drops for any reason, then
/// reconnects with exponential backoff+jitter (same BackoffPolicy the
/// heartbeat/outbox already use). TCP keepalive is enabled so a genuinely
/// dead link (cable pulled, device rebooted) is detected by the OS within a
/// bounded time — there is deliberately no app-level idle-read timeout,
/// since this is a push-only protocol with legitimately long quiet periods
/// between measurements.
/// </summary>
public sealed class EasyCubeTcpListener : BackgroundService, IEasyCubeConnection
{
    private readonly EasyCubeTcpOptions _options;
    private readonly ILogger<EasyCubeTcpListener> _logger;
    private readonly Random _random = new();

    public EasyCubeConnectionState State { get; private set; } = EasyCubeConnectionState.Disconnected;
    public event Action<EasyCubeConnectionState>? StateChanged;
    public event Func<CapturedMeasurement, CancellationToken, Task>? MeasurementReceived;

    public EasyCubeTcpListener(IOptions<EasyCubeTcpOptions> options, ILogger<EasyCubeTcpListener> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            SetState(consecutiveFailures == 0 ? EasyCubeConnectionState.Connecting : EasyCubeConnectionState.Reconnecting);

            try
            {
                using var client = new TcpClient();
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    connectCts.CancelAfter(_options.ConnectTimeout);
                    await client.ConnectAsync(_options.Host, _options.Port, connectCts.Token);
                }

                consecutiveFailures = 0;
                SetState(EasyCubeConnectionState.Connected);
                _logger.LogInformation("Connected to EasyCube at {Host}:{Port}", _options.Host, _options.Port);

                await ReadLoopAsync(client, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
            {
                consecutiveFailures++;
                _logger.LogWarning("EasyCube TCP connection failed/dropped ({Failures} consecutive): {ErrorType}", consecutiveFailures, ex.GetType().Name);
            }

            if (stoppingToken.IsCancellationRequested) break;

            SetState(EasyCubeConnectionState.Reconnecting);
            var backoff = BackoffPolicy.Compute(consecutiveFailures, _options.InitialBackoff, _options.MaxBackoff, _random);
            await SafeDelay(backoff, stoppingToken);
        }

        SetState(EasyCubeConnectionState.Disconnected);
    }

    /// <summary>
    /// Reads raw bytes until the connection drops, buffering across reads so
    /// a frame split by TCP fragmentation is reassembled before parsing, and
    /// draining every complete frame found in a single read (the device may
    /// have queued more than one measurement) before waiting for more data.
    /// </summary>
    private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var receiveBuffer = new byte[8192];
        var textBuffer = "";

        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(receiveBuffer, ct);
            if (read == 0)
            {
                throw new IOException("EasyCube closed the connection");
            }

            textBuffer += Encoding.ASCII.GetString(receiveBuffer, 0, read);
            var (frames, remainder) = EasyCubeProtocolZeroParser.ExtractFrames(textBuffer);
            textBuffer = remainder;

            foreach (var frame in frames)
            {
                await HandleFrameAsync(frame, ct);
            }
        }
    }

    private async Task HandleFrameAsync(string frame, CancellationToken ct)
    {
        var parsed = EasyCubeProtocolZeroParser.TryParse(frame);
        if (parsed is not EasyCubeFrameParseResult.Ok ok)
        {
            var detail = parsed is EasyCubeFrameParseResult.Malformed malformed ? malformed.Detail : "unknown";
            _logger.LogWarning("Discarding malformed EasyCube TCP frame: {Detail}", detail);
            return;
        }

        var record = ok.Record;

        var weight = UnitConverter.ParseWeightToKg(record.Weight, record.WeightUnit);
        var length = UnitConverter.ParseLengthToCm(record.Length, record.LengthUnit);
        var width = UnitConverter.ParseLengthToCm(record.Width, record.WidthUnit);
        var height = UnitConverter.ParseLengthToCm(record.Height, record.HeightUnit);
        if (weight is not UnitParseResult.Ok weightOk || length is not UnitParseResult.Ok lengthOk
            || width is not UnitParseResult.Ok widthOk || height is not UnitParseResult.Ok heightOk)
        {
            _logger.LogWarning("Discarding EasyCube TCP frame for package {PackageNumber} — unreadable units", record.PackageNumber);
            return;
        }

        decimal? dimWeightKg = null;
        if (record.DimensionalWeight is decimal rawDimWeight && rawDimWeight > 0)
        {
            var dimWeight = UnitConverter.ParseWeightToKg(rawDimWeight, record.DimensionalWeightUnit);
            if (dimWeight is UnitParseResult.Ok dimOk) dimWeightKg = dimOk.Value;
        }

        var measurement = new CapturedMeasurement
        {
            DeviceId = record.DeviceSerial,
            PackageNumber = record.PackageNumber,
            Timestamp = ParseDeviceTimestamp(record.TimestampRaw),
            WeightKg = weightOk.Value,
            LengthCm = lengthOk.Value,
            WidthCm = widthOk.Value,
            HeightCm = heightOk.Value,
            DimensionalWeightKg = dimWeightKg,
            DeviceReportedBarcode = record.Barcode,
        };

        var handler = MeasurementReceived;
        if (handler is not null)
        {
            await handler(measurement, ct);
        }
    }

    /// <summary>Same local-time assumption as EasyCubeClient's HTTP path (see its class doc) — the TCP protocol's "T" field uses the identical "yyyy-MM-dd HH:mm:ss" shape with no explicit timezone.</summary>
    private DateTimeOffset ParseDeviceTimestamp(string raw)
    {
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
        {
            return new DateTimeOffset(parsed.ToUniversalTime());
        }

        _logger.LogWarning("EasyCube TCP timestamp '{Raw}' did not parse — treating capture as 'now'", raw);
        return DateTimeOffset.UtcNow;
    }

    private void SetState(EasyCubeConnectionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }
}
