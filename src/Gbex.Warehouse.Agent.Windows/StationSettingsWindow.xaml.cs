using System.Windows;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Infrastructure.Gbex;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gbex.Warehouse.Agent.Windows;

/// <summary>
/// First-run / ongoing station configuration. The station secret is written
/// via ISecretStore (DPAPI on the real Windows build) and NEVER redisplayed
/// after Save — the PasswordBox is cleared immediately, and
/// TryGetStationSecretAsync is never called just to show it back to the
/// operator. Exception messages shown here are the redacted, human-readable
/// GbexApiResult reasons only — never a raw exception with header/secret
/// content.
/// </summary>
public partial class StationSettingsWindow : Window
{
    private readonly ISecretStore _secretStore;
    private readonly AgentSettingsStore _settingsStore;
    private readonly ILoggerFactory _loggerFactory;

    public StationSettingsWindow(ISecretStore secretStore, AgentSettingsStore settingsStore, ILoggerFactory loggerFactory)
    {
        InitializeComponent();
        _secretStore = secretStore;
        _settingsStore = settingsStore;
        _loggerFactory = loggerFactory;

        var settings = _settingsStore.Load();
        GbexBaseUrlBox.Text = settings.GbexApiBaseUrl;
        EasyCubeBaseUrlBox.Text = settings.EasyCubeBaseUrl;
        DeviceIdBox.Text = settings.DeviceId ?? "";
        RefreshSecretStatus();
    }

    private void RefreshSecretStatus()
    {
        var has = _secretStore.HasStationSecretAsync(CancellationToken.None).GetAwaiter().GetResult();
        SecretStatusText.Text = has ? "İstasyon anahtarı kayıtlı." : "İstasyon anahtarı tanımlı değil.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = new AgentSettings
        {
            GbexApiBaseUrl = GbexBaseUrlBox.Text.Trim(),
            EasyCubeBaseUrl = EasyCubeBaseUrlBox.Text.Trim(),
            DeviceId = string.IsNullOrWhiteSpace(DeviceIdBox.Text) ? null : DeviceIdBox.Text.Trim(),
        };
        _settingsStore.Save(settings);

        var secret = StationSecretBox.Password;
        if (!string.IsNullOrWhiteSpace(secret))
        {
            await _secretStore.SaveStationSecretAsync(secret, CancellationToken.None);
        }
        // Cleared immediately regardless — never left sitting in the UI, and
        // never read back out to redisplay it.
        StationSecretBox.Clear();

        RefreshSecretStatus();
        ResultText.Text = "Kaydedildi. Değişikliklerin etkili olması için Ajanı yeniden başlatın.";
    }

    private async void RemoveCredential_Click(object sender, RoutedEventArgs e)
    {
        await _secretStore.RemoveStationSecretAsync(CancellationToken.None);
        RefreshSecretStatus();
        ResultText.Text = "İstasyon kimlik bilgisi kaldırıldı.";
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        ResultText.Text = "Test ediliyor…";
        var settings = new AgentSettings { GbexApiBaseUrl = GbexBaseUrlBox.Text.Trim() };
        if (!settings.IsConfigured)
        {
            ResultText.Text = "Önce GBEX API adresini girin.";
            return;
        }

        try
        {
            var options = Options.Create(new GbexApiOptions { BaseUrl = settings.GbexApiBaseUrl });
            var client = new GbexApiClient(new HttpClient(), options, _secretStore, _loggerFactory.CreateLogger<GbexApiClient>());
            var result = await client.HeartbeatAsync("test-connection", CancellationToken.None);

            ResultText.Text = result switch
            {
                HeartbeatOutcome ok => $"Başarılı — istasyon: {ok.StationName}",
                GbexApiResult.Unauthorized => "Yetkisiz — istasyon anahtarını kontrol edin.",
                GbexApiResult.StationDisabled => "İstasyon devre dışı.",
                GbexApiResult.TransientFailure => "Sunucuya ulaşılamadı.",
                _ => "Bilinmeyen sonuç.",
            };
        }
        catch (InvalidOperationException ex)
        {
            // e.g. the HTTPS-outside-development guard in GbexApiClient's
            // constructor — its message is already safe to show verbatim
            // (it names the offending scheme/host, never a secret).
            ResultText.Text = ex.Message;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
