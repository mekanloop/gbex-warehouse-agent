using System.Net.Http;
using System.Windows;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Infrastructure.Configuration;
using Gbex.Warehouse.Agent.Infrastructure.EasyCube;
using Gbex.Warehouse.Agent.Infrastructure.Gbex;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gbex.Warehouse.Agent.Windows;

/// <summary>
/// First-run / ongoing station configuration. The station secret is written
/// via ISecretStore (DPAPI on the real Windows build) and NEVER redisplayed
/// after Save — the PasswordBox is cleared immediately, and
/// TryGetStationSecretAsync is never called just to show it back to the
/// operator. Two SEPARATE "Bağlantıyı Test Et" buttons — one per system —
/// so a non-technical operator can tell exactly which connection (GBEX vs.
/// the EasyCube device) is the problem, rather than one combined test that
/// hides which side actually failed. Exception messages shown here are the
/// redacted, human-readable GbexApiResult/EasyCubeResult reasons only —
/// never a raw exception with header/secret content.
/// </summary>
public partial class StationSettingsWindow : Window
{
    private readonly ISecretStore _secretStore;
    private readonly AgentSettingsStore _settingsStore;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Set true when shown automatically because the Agent has never been configured — shows a welcoming banner instead of the bare settings form.</summary>
    public bool IsFirstRun
    {
        set => WelcomeText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public StationSettingsWindow(ISecretStore secretStore, AgentSettingsStore settingsStore, ILoggerFactory loggerFactory)
    {
        InitializeComponent();
        _secretStore = secretStore;
        _settingsStore = settingsStore;
        _loggerFactory = loggerFactory;

        var settings = _settingsStore.Load();
        GbexBaseUrlBox.Text = settings.GbexApiBaseUrl;
        EasyCubeTcpHostBox.Text = settings.EasyCubeTcpHost;
        EasyCubeTcpPortBox.Text = settings.EasyCubeTcpPort > 0 ? settings.EasyCubeTcpPort.ToString() : "9990";
        EasyCubeBaseUrlBox.Text = settings.EasyCubeBaseUrl;
        DeviceIdBox.Text = settings.DeviceId ?? "";
        RefreshSecretStatus();
    }

    private void RefreshSecretStatus()
    {
        var has = _secretStore.HasStationSecretAsync(CancellationToken.None).GetAwaiter().GetResult();
        SecretStatusText.Text = has ? "Durum: İstasyon anahtarı kayıtlı." : "Durum: İstasyon anahtarı tanımlı değil.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GbexBaseUrlBox.Text) || string.IsNullOrWhiteSpace(EasyCubeTcpHostBox.Text))
        {
            ResultText.Foreground = System.Windows.Media.Brushes.DarkRed;
            ResultText.Text = "GBEX sunucu adresi ve EasyCube IP adresi zorunludur.";
            return;
        }

        if (!int.TryParse(EasyCubeTcpPortBox.Text.Trim(), out var tcpPort) || tcpPort is <= 0 or > 65535)
        {
            ResultText.Foreground = System.Windows.Media.Brushes.DarkRed;
            ResultText.Text = "EasyCube TCP portu geçersiz. Örnek: 9990";
            return;
        }

        var settings = new AgentSettings
        {
            GbexApiBaseUrl = GbexBaseUrlBox.Text.Trim(),
            EasyCubeTcpHost = EasyCubeTcpHostBox.Text.Trim(),
            EasyCubeTcpPort = tcpPort,
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
        ResultText.Foreground = System.Windows.Media.Brushes.DarkGreen;
        ResultText.Text = "Kaydedildi. Değişikliklerin etkili olması için Ajanı yeniden başlatın.";
    }

    private async void RemoveCredential_Click(object sender, RoutedEventArgs e)
    {
        await _secretStore.RemoveStationSecretAsync(CancellationToken.None);
        RefreshSecretStatus();
        ResultText.Foreground = System.Windows.Media.Brushes.Black;
        ResultText.Text = "İstasyon kimlik bilgisi kaldırıldı.";
    }

    private async void TestGbexConnection_Click(object sender, RoutedEventArgs e)
    {
        GbexTestResultText.Text = "Test ediliyor…";
        var baseUrl = GbexBaseUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            GbexTestResultText.Text = "Önce GBEX sunucu adresini girin.";
            return;
        }

        try
        {
            var options = Options.Create(new GbexApiOptions { BaseUrl = baseUrl });
            var client = new GbexApiClient(new HttpClient(), options, _secretStore, _loggerFactory.CreateLogger<GbexApiClient>());
            var result = await client.HeartbeatAsync("test-connection", CancellationToken.None);

            GbexTestResultText.Text = result switch
            {
                HeartbeatOutcome ok => $"✓ Başarılı — istasyon: {ok.StationName}",
                GbexApiResult.Unauthorized => "✗ Yetkisiz — istasyon anahtarını kontrol edin.",
                GbexApiResult.StationDisabled => "✗ İstasyon devre dışı.",
                GbexApiResult.TransientFailure => "✗ Sunucuya ulaşılamadı. Adresi ve internet bağlantısını kontrol edin.",
                _ => "✗ Bilinmeyen sonuç.",
            };
        }
        catch (InvalidOperationException ex)
        {
            // e.g. the HTTPS-outside-development guard in GbexApiClient's
            // constructor — its message is already safe to show verbatim
            // (it names the offending scheme/host, never a secret).
            GbexTestResultText.Text = $"✗ {ex.Message}";
        }
    }

    private async void TestEasyCubeTcpConnection_Click(object sender, RoutedEventArgs e)
    {
        EasyCubeTcpTestResultText.Text = "Test ediliyor…";
        var host = EasyCubeTcpHostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            EasyCubeTcpTestResultText.Text = "Önce EasyCube IP adresini girin.";
            return;
        }
        if (!int.TryParse(EasyCubeTcpPortBox.Text.Trim(), out var port) || port is <= 0 or > 65535)
        {
            EasyCubeTcpTestResultText.Text = "Geçersiz TCP portu. Örnek: 9990";
            return;
        }

        var result = await EasyCubeTcpProbe.TestAsync(host, port, TimeSpan.FromSeconds(5), CancellationToken.None);
        EasyCubeTcpTestResultText.Text = result switch
        {
            EasyCubeTcpProbeResult.Ok ok => $"✓ Bağlantı başarılı — cihaz: {ok.DeviceModel}",
            EasyCubeTcpProbeResult.Unreachable => "✗ Cihaza ulaşılamadı. IP adresini, portu ve ağ/switch bağlantısını kontrol edin.",
            EasyCubeTcpProbeResult.Timeout => "✗ Cihaz yanıt vermedi (zaman aşımı).",
            EasyCubeTcpProbeResult.UnexpectedResponse => "✗ Cihazdan beklenmeyen bir yanıt alındı.",
            _ => "✗ Bilinmeyen sonuç.",
        };
    }

    private async void TestEasyCubeConnection_Click(object sender, RoutedEventArgs e)
    {
        EasyCubeTestResultText.Text = "Test ediliyor…";
        var baseUrl = EasyCubeBaseUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            EasyCubeTestResultText.Text = "Önce EasyCube cihaz adresini girin.";
            return;
        }

        try
        {
            var options = Options.Create(new EasyCubeOptions { BaseUrl = baseUrl });
            var client = new EasyCubeClient(new HttpClient(), options, _loggerFactory.CreateLogger<EasyCubeClient>());
            var result = await client.GetDeviceInfoAsync(CancellationToken.None);

            EasyCubeTestResultText.Text = result switch
            {
                DeviceHealth h => $"✓ Bağlantı başarılı — cihaz: {h.Info!.DeviceModel}",
                EasyCubeResult.Unreachable => "✗ Cihaz bulunamadı. Adresi ve ağ bağlantısını kontrol edin.",
                EasyCubeResult.Timeout => "✗ Cihaz yanıt vermedi (zaman aşımı).",
                EasyCubeResult.DeviceError err => $"✗ Cihaz hatası: {err.Message}",
                EasyCubeResult.MalformedResponse => "✗ Cihazdan beklenmeyen bir yanıt alındı.",
                _ => "✗ Bilinmeyen sonuç.",
            };
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            EasyCubeTestResultText.Text = "✗ Geçersiz adres. Örnek: http://192.168.1.50:8080";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
