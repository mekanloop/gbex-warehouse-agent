namespace Gbex.Warehouse.Agent.Infrastructure.EasyCube;

public sealed class EasyCubeOptions
{
    /// <summary>e.g. http://192.168.1.50:8080 or https://... if the device is configured for HttpsInUse (see /websconfig).</summary>
    public required string BaseUrl { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(8);
}
