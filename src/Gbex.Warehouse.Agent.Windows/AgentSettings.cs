using System.IO;
using System.Text.Json;

namespace Gbex.Warehouse.Agent.Windows;

/// <summary>
/// Non-secret local configuration — everything EXCEPT the station secret,
/// which lives only in DpapiSecretStore. Plain JSON on disk is fine for
/// these fields; none of them are sensitive.
/// </summary>
public sealed class AgentSettings
{
    public string GbexApiBaseUrl { get; set; } = "";
    public string EasyCubeBaseUrl { get; set; } = "";
    public string? DeviceId { get; set; }
    public bool AllowInsecureGbexForDevelopment { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(GbexApiBaseUrl) && !string.IsNullOrWhiteSpace(EasyCubeBaseUrl);
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
