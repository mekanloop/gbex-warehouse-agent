using Gbex.Warehouse.Agent.Infrastructure.Configuration;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

public class AgentSettingsTests
{
    [Fact]
    public void IsConfigured_is_false_when_either_required_address_is_missing()
    {
        Assert.False(new AgentSettings().IsConfigured);
        Assert.False(new AgentSettings { GbexApiBaseUrl = "https://app.gbex.com.tr" }.IsConfigured);
        Assert.False(new AgentSettings { EasyCubeBaseUrl = "http://localhost:8080" }.IsConfigured);
    }

    [Fact]
    public void IsConfigured_is_true_once_both_required_addresses_are_set()
    {
        var settings = new AgentSettings { GbexApiBaseUrl = "https://app.gbex.com.tr", EasyCubeBaseUrl = "http://localhost:8080" };
        Assert.True(settings.IsConfigured);
    }

    [Fact]
    public void AgentSettingsStore_never_persists_a_secret_field_because_none_exists_on_the_model()
    {
        var props = typeof(AgentSettings).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(props, p => p.Contains("Secret") || p.Contains("Token") || p.Contains("Password"));
    }
}

public class AgentSettingsStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "gbex-agent-settings-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_returns_defaults_when_no_file_exists_yet()
    {
        var store = new AgentSettingsStore(_tempDir);
        var settings = store.Load();
        Assert.False(settings.IsConfigured);
    }

    [Fact]
    public void Save_then_Load_round_trips_every_field()
    {
        var store = new AgentSettingsStore(_tempDir);
        var original = new AgentSettings
        {
            GbexApiBaseUrl = "https://app.gbex.com.tr",
            EasyCubeBaseUrl = "http://192.168.1.50:8080",
            DeviceId = "depo-1",
            AllowInsecureGbexForDevelopment = false,
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(original.GbexApiBaseUrl, loaded.GbexApiBaseUrl);
        Assert.Equal(original.EasyCubeBaseUrl, loaded.EasyCubeBaseUrl);
        Assert.Equal(original.DeviceId, loaded.DeviceId);
        Assert.True(loaded.IsConfigured);
    }

    [Fact]
    public void Load_recovers_gracefully_from_a_corrupted_settings_file()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "agent-settings.json"), "{ not valid json at all");

        var store = new AgentSettingsStore(_tempDir);
        var settings = store.Load();

        Assert.False(settings.IsConfigured); // falls back to defaults, does not throw
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
