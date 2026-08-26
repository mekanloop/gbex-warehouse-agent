namespace Gbex.Warehouse.Agent.Infrastructure.Gbex;

public sealed class GbexApiOptions
{
    /// <summary>e.g. https://app.gbex.com.tr — must be HTTPS outside development (enforced in GbexApiClient's constructor, not just documented).</summary>
    public required string BaseUrl { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Set true only for local development against http://localhost — refused for any non-loopback, non-HTTPS URL.</summary>
    public bool AllowInsecureForDevelopment { get; init; } = false;
}
