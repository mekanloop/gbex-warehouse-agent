namespace Gbex.Warehouse.Agent.Infrastructure.EasyCube;

public sealed class EasyCubeTcpOptions
{
    /// <summary>e.g. 192.168.1.50 — the device's IP address on the warehouse LAN, entered on the first-run screen.</summary>
    public required string Host { get; init; }

    /// <summary>The device's configured TCP/IP server port (see the guide's TCPS command — default example is 9990, but this is device-configurable via its own Web UI/API).</summary>
    public required int Port { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);
}
