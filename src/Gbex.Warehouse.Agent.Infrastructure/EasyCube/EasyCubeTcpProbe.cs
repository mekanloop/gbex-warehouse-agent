using System.IO;
using System.Net.Sockets;
using System.Text;
using Gbex.Warehouse.Agent.Core.EasyCube;

namespace Gbex.Warehouse.Agent.Infrastructure.EasyCube;

public abstract record EasyCubeTcpProbeResult
{
    /// <summary>
    /// ImageScalePercent is the device's own TCPS "IS" setting (see
    /// docs/EASYCUBE_CONTRACT.md — manufacturer's own worked example shows
    /// "IS,25", i.e. the device captures at 25% scale) when the follow-up
    /// TCPS query succeeded, or null if that second query failed/timed out
    /// (DVM having already succeeded is not downgraded to a failure just
    /// because this best-effort extra read didn't land).
    /// </summary>
    public sealed record Ok(string DeviceModel, int? ImageScalePercent) : EasyCubeTcpProbeResult;
    public sealed record Unreachable(string Detail) : EasyCubeTcpProbeResult;
    public sealed record Timeout : EasyCubeTcpProbeResult;
    public sealed record UnexpectedResponse(string Raw) : EasyCubeTcpProbeResult;
}

/// <summary>
/// One-shot "Bağlantıyı Test Et" connectivity probe for the settings screen —
/// a completely separate, throwaway TCP connection from EasyCubeTcpListener's
/// persistent one. Sends the manufacturer's documented "DVM" (get device
/// model) command and reads back its identity, giving the operator a real
/// device-identity confirmation rather than just "the port is open".
///
/// Also opportunistically queries "TCPS" on the same connection to read the
/// device's own configured image-scale ("IS") setting — this is the actual
/// root cause of low-resolution warehouse evidence photos (see the image
/// quality audit): the device captures and transmits images at whatever
/// scale IS specifies, and nothing in this Agent's own code path resizes or
/// recompresses evidence images at any stage. Surfacing IS here lets an
/// operator/admin catch a badly-configured station (IS well below 100)
/// without hunting for it — the actual fix is still done on the device's own
/// Web UI/API (TCPS is documented as "configured on the device itself...
/// not by this Agent"), never here.
/// </summary>
public static class EasyCubeTcpProbe
{
    /// <summary>Below this, evidence photos are visibly degraded — chosen as "well under half native resolution", not a manufacturer-documented threshold.</summary>
    public const int LowImageScaleThreshold = 50;

    public static async Task<EasyCubeTcpProbeResult> TestAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(host, port, cts.Token);

            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("DVM\r\n"), cts.Token);

            var buffer = new byte[512];
            var read = await stream.ReadAsync(buffer, cts.Token);
            var raw = Encoding.ASCII.GetString(buffer, 0, read);

            var (frames, _) = EasyCubeProtocolZeroParser.ExtractFrames(raw);
            if (frames.Count == 0)
            {
                return new EasyCubeTcpProbeResult.UnexpectedResponse(raw);
            }

            var tokens = frames[0].Split(',');
            if (tokens.Length < 2 || !tokens[0].Trim().Equals("DVM", StringComparison.OrdinalIgnoreCase))
            {
                return new EasyCubeTcpProbeResult.UnexpectedResponse(raw);
            }

            var deviceModel = tokens[1].Trim();
            var imageScale = await TryReadImageScaleAsync(stream, cts.Token);
            return new EasyCubeTcpProbeResult.Ok(deviceModel, imageScale);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new EasyCubeTcpProbeResult.Timeout();
        }
        catch (SocketException ex)
        {
            return new EasyCubeTcpProbeResult.Unreachable(ex.SocketErrorCode.ToString());
        }
    }

    /// <summary>
    /// Best-effort only — a device that doesn't answer TCPS (or answers with
    /// something this doesn't recognize) must never fail the whole probe,
    /// since DVM already proved the connection and device identity are fine.
    /// </summary>
    private static async Task<int?> TryReadImageScaleAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("TCPS\r\n"), ct);

            var buffer = new byte[512];
            var read = await stream.ReadAsync(buffer, ct);
            var raw = Encoding.ASCII.GetString(buffer, 0, read);

            var (frames, _) = EasyCubeProtocolZeroParser.ExtractFrames(raw);
            if (frames.Count == 0) return null;

            var tokens = frames[0].Split(',');
            if (tokens.Length == 0 || !tokens[0].Trim().Equals("TCPS", StringComparison.OrdinalIgnoreCase)) return null;

            for (var i = 1; i + 1 < tokens.Length; i += 2)
            {
                if (tokens[i].Trim().Equals("IS", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(tokens[i + 1].Trim(), out var scale))
                {
                    return scale;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
        {
            return null;
        }
    }
}
