using System.Text.Json;

namespace Gbex.Warehouse.Agent.Infrastructure.Configuration;

/// <summary>
/// Non-secret local configuration — everything EXCEPT the station secret,
/// which lives only in ISecretStore/DpapiSecretStore. Plain JSON on disk is
/// fine for these fields; none of them are sensitive. Lives here (not in
/// the Windows project) because it has no WPF/Windows dependency at all —
/// keeping it in the cross-platform layer is what makes it unit-testable.
/// </summary>
public sealed class AgentSettings
{
    public string GbexApiBaseUrl { get; set; } = "";

    /// <summary>PRIMARY EasyCube connection: the device's IP on the warehouse LAN, e.g. "192.168.1.50". See EasyCubeTcpPort for the matching TCP/IP server port.</summary>
    public string EasyCubeTcpHost { get; set; } = "";

    /// <summary>PRIMARY EasyCube connection's TCP/IP server port (the guide's TCPS command — device-configurable, default example 9990).</summary>
    public int EasyCubeTcpPort { get; set; } = 9990;

    /// <summary>OPTIONAL fallback only — EasyCube's HTTP Web API base URL, used solely by the manual keyboard-wedge flow when the primary TCP push connection isn't configured/working. Never required for first-run to complete.</summary>
    public string EasyCubeBaseUrl { get; set; } = "";

    public string? DeviceId { get; set; }
    public bool AllowInsecureGbexForDevelopment { get; set; }

    /// <summary>The fallback HTTP address is deliberately excluded — first-run must not block on an optional, secondary connection.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(GbexApiBaseUrl)
        && !string.IsNullOrWhiteSpace(EasyCubeTcpHost)
        && EasyCubeTcpPort is > 0 and <= 65535;
}

public sealed class AgentSettingsStore
{
    private readonly string _filePath;

    public AgentSettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "agent-settings.json");
    }

    public AgentSettings Load()
    {
        if (!File.Exists(_filePath)) return new AgentSettings();
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AgentSettings>(json) ?? new AgentSettings();
        }
        catch (JsonException)
        {
            return new AgentSettings();
        }
    }

    public void Save(AgentSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}
