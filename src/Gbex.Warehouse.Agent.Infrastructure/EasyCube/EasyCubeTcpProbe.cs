using System.Net.Sockets;
using System.Text;
using Gbex.Warehouse.Agent.Core.EasyCube;

namespace Gbex.Warehouse.Agent.Infrastructure.EasyCube;

public abstract record EasyCubeTcpProbeResult
{
    public sealed record Ok(string DeviceModel) : EasyCubeTcpProbeResult;
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
/// </summary>
public static class EasyCubeTcpProbe
{
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
            if (tokens.Length >= 2 && tokens[0].Trim().Equals("DVM", StringComparison.OrdinalIgnoreCase))
            {
                return new EasyCubeTcpProbeResult.Ok(tokens[1].Trim());
            }

            return new EasyCubeTcpProbeResult.UnexpectedResponse(raw);
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
}
