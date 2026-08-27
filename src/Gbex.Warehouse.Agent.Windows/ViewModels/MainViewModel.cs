using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Barcode;
using Gbex.Warehouse.Agent.Core.Models;
using Gbex.Warehouse.Agent.Core.Workflow;
using Gbex.Warehouse.Agent.Infrastructure.Configuration;
using Gbex.Warehouse.Agent.Infrastructure.Diagnostics;
using Gbex.Warehouse.Agent.Infrastructure.Gbex;
using Gbex.Warehouse.Agent.Infrastructure.Heartbeat;

namespace Gbex.Warehouse.Agent.Windows.ViewModels;

/// <summary>
/// The single view model behind MainWindow. Deliberately thin: every actual
/// decision (lookup, measure, submit, PASS/MISMATCH handling) happens in
/// WarehouseWorkflowEngine — this class only calls into it, marshals state
/// changes onto the UI thread via the Dispatcher, and renders whatever
/// comes back. No HTTP, no SQLite, no business rule lives here.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly WarehouseWorkflowEngine _engine;
    private readonly HeartbeatService _heartbeat;
    private readonly IOutboxStore _outbox;
    private readonly IEasyCubeClient _easyCubeClient;
    private readonly IEasyCubeConnection _easyCubeConnection;
    private readonly ISecretStore _secretStore;
    private readonly AgentSettings _settings;
    private readonly ScanDebouncer _debouncer;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _queueCountTimer;
    private readonly DispatcherTimer _easyCubeHealthTimer;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string AgentVersion { get; }
    public string StationStatusText => _heartbeat.State switch
    {
        StationConnectionState.Connected => "Bağlı",
        StationConnectionState.Degraded => "Zayıf bağlantı",
        StationConnectionState.Offline => "Çevrimdışı",
        StationConnectionState.Unauthorized => "Yetkisiz — istasyon anahtarını kontrol edin",
        StationConnectionState.Disabled => "İstasyon devre dışı",
        _ => "Bilinmiyor",
    };

    public DateTimeOffset? LastHeartbeatAt => _heartbeat.LastSuccessfulHeartbeatAt;

    /// <summary>Primary connection status — driven by the persistent EasyCube TCP link, not the optional HTTP fallback (see EasyCubeFallbackStatusText for that).</summary>
    public string EasyCubeStatusText => _easyCubeConnection.State switch
    {
        EasyCubeConnectionState.Connected => "Bağlı — otomatik ölçüm bekleniyor",
        EasyCubeConnectionState.Connecting => "Bağlanıyor…",
        EasyCubeConnectionState.Reconnecting => "Bağlantı kesildi — yeniden bağlanılıyor…",
        EasyCubeConnectionState.Disconnected => "Bağlı değil",
        _ => "Bilinmiyor",
    };

    private string? _easyCubeDeviceModel;
    public string? EasyCubeDeviceModel { get => _easyCubeDeviceModel; private set { _easyCubeDeviceModel = value; Raise(); } }

    private string? _easyCubeSoftwareVersion;
    public string? EasyCubeSoftwareVersion { get => _easyCubeSoftwareVersion; private set { _easyCubeSoftwareVersion = value; Raise(); } }

    /// <summary>Only meaningful when a fallback HTTP address is configured — "" otherwise, and the UI hides the fallback tile entirely in that case.</summary>
    private string _easyCubeFallbackStatusText = "";
    public string EasyCubeFallbackStatusText
    {
        get => _easyCubeFallbackStatusText;
        private set { _easyCubeFallbackStatusText = value; Raise(); }
    }

    public bool EasyCubeFallbackConfigured => !string.IsNullOrWhiteSpace(_settings.EasyCubeBaseUrl);

    private AgentWorkflowState _workflowState = AgentWorkflowState.Ready;
    public AgentWorkflowState WorkflowState
    {
        get => _workflowState;
        private set { _workflowState = value; Raise(); Raise(nameof(WorkflowStatusText)); }
    }

    public string WorkflowStatusText => WorkflowState switch
    {
        AgentWorkflowState.Ready => "Hazır — barkod okutun",
        AgentWorkflowState.LookingUpOrder => "Gönderi aranıyor…",
        AgentWorkflowState.OrderFound => "Gönderi bulundu",
        AgentWorkflowState.Measuring => "Ölçülüyor…",
        AgentWorkflowState.Submitting => "Gönderiliyor…",
        AgentWorkflowState.VerifiedPass => "DOĞRULANDI — UYGUN",
        AgentWorkflowState.OnHoldMismatch => "BEKLEMEDE — OPERATÖR ÇÖZÜMÜ GEREKLİ",
        AgentWorkflowState.OfflineQueued => "ÇEVRİMDIŞI — KUYRUĞA ALINDI",
        AgentWorkflowState.StationUnauthorized => "İSTASYON YETKİSİZ",
        AgentWorkflowState.StationDisabled => "İSTASYON DEVRE DIŞI",
        AgentWorkflowState.EasyCubeError => "CİHAZ HATASI",
        _ => "",
    };

    private string _scanInput = "";
    public string ScanInput
    {
        get => _scanInput;
        set { _scanInput = value; Raise(); }
    }

    private StationOrderDto? _currentOrder;
    public StationOrderDto? CurrentOrder
    {
        get => _currentOrder;
        private set { _currentOrder = value; Raise(); }
    }

    private string? _lastMeasurementSummary;
    public string? LastMeasurementSummary
    {
        get => _lastMeasurementSummary;
        private set { _lastMeasurementSummary = value; Raise(); }
    }

    private int _offlineQueueCount;
    public int OfflineQueueCount
    {
        get => _offlineQueueCount;
        private set { _offlineQueueCount = value; Raise(); }
    }

    public MainViewModel(
        WarehouseWorkflowEngine engine,
        HeartbeatService heartbeat,
        IOutboxStore outbox,
        IEasyCubeClient easyCubeClient,
        IEasyCubeConnection easyCubeConnection,
        ISecretStore secretStore,
        AgentSettings settings,
        IClock clock,
        string agentVersion)
    {
        _engine = engine;
        _heartbeat = heartbeat;
        _outbox = outbox;
        _easyCubeClient = easyCubeClient;
        _easyCubeConnection = easyCubeConnection;
        _secretStore = secretStore;
        _settings = settings;
        _debouncer = new ScanDebouncer(clock);
        AgentVersion = agentVersion;
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _engine.StateChanged += OnEngineStateChanged;
        _heartbeat.StateChanged += OnHeartbeatStateChanged;
        _easyCubeConnection.StateChanged += OnEasyCubeConnectionStateChanged;
        _easyCubeConnection.MeasurementReceived += OnDeviceMeasurementReceivedAsync;

        _queueCountTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _queueCountTimer.Tick += async (_, _) => await RefreshQueueCountAsync();
        _queueCountTimer.Start();

        // The periodic HTTP health poll only matters for the OPTIONAL
        // fallback path now — the primary "EasyCube Cihazı" tile is driven
        // by the persistent TCP connection's own StateChanged event instead
        // (see OnEasyCubeConnectionStateChanged), not a poll.
        _easyCubeHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _easyCubeHealthTimer.Tick += async (_, _) => await RefreshEasyCubeFallbackHealthAsync();
        if (EasyCubeFallbackConfigured)
        {
            _easyCubeHealthTimer.Start();
            _ = RefreshEasyCubeFallbackHealthAsync();
        }
    }

    private void OnEngineStateChanged(AgentWorkflowState state) =>
        _dispatcher.Invoke(() => WorkflowState = state);

    private void OnHeartbeatStateChanged(StationConnectionState _) =>
        _dispatcher.Invoke(() => { Raise(nameof(StationStatusText)); Raise(nameof(LastHeartbeatAt)); });

    private void OnEasyCubeConnectionStateChanged(EasyCubeConnectionState _) =>
        _dispatcher.Invoke(() => Raise(nameof(EasyCubeStatusText)));

    private async Task RefreshQueueCountAsync()
    {
        var count = await _outbox.CountPendingAsync(CancellationToken.None);
        OfflineQueueCount = count;
    }

    /// <summary>
    /// PRIMARY entry point — invoked automatically whenever EasyCube pushes a
    /// combined barcode+measurement record over the TCP connection. No
    /// operator action required: the barcode scanner is wired into EasyCube
    /// itself, not the PC. Runs on whatever thread the TCP read loop is on,
    /// so every UI-bound update is marshalled through the Dispatcher.
    /// </summary>
    private async Task OnDeviceMeasurementReceivedAsync(CapturedMeasurement measurement, CancellationToken ct)
    {
        var result = await _engine.HandleDeviceMeasurementAsync(measurement, ct);
        _dispatcher.Invoke(() =>
        {
            CurrentOrder = result.Order;
            LastMeasurementSummary = Describe(result.Outcome);
        });
        await RefreshQueueCountAsync();
    }

    /// <summary>
    /// Clear Turkish status for the OPTIONAL HTTP fallback link only — the
    /// primary EasyCube status comes from the TCP connection's own
    /// StateChanged event, not this poll. Never runs if no fallback address
    /// is configured (the common case now that TCP push is the default).
    /// </summary>
    private async Task RefreshEasyCubeFallbackHealthAsync()
    {
        var result = await _easyCubeClient.GetDeviceInfoAsync(CancellationToken.None);
        var (text, model, version) = result switch
        {
            DeviceHealth h => ($"Yedek (HTTP) bağlı ({h.Info!.DeviceModel})", h.Info.DeviceModel, h.Info.SoftwareVersion),
            EasyCubeResult.Unreachable => ("Yedek (HTTP) bulunamadı", (string?)null, (string?)null),
            EasyCubeResult.Timeout => ("Yedek (HTTP) zaman aşımı", (string?)null, (string?)null),
            EasyCubeResult.DeviceError err => ($"Yedek (HTTP) cihaz hatası: {err.Message}", (string?)null, (string?)null),
            EasyCubeResult.MalformedResponse => ("Yedek (HTTP) beklenmeyen yanıt", (string?)null, (string?)null),
            _ => ("Bilinmiyor", (string?)null, (string?)null),
        };
        _dispatcher.Invoke(() =>
        {
            EasyCubeFallbackStatusText = text;
            EasyCubeDeviceModel = model;
            EasyCubeSoftwareVersion = version;
        });
    }

    /// <summary>Called from the scan textbox's Enter-key handler — the ONLY entry point that starts a new lookup/measure cycle.</summary>
    public async Task OnBarcodeScannedAsync()
    {
        var raw = ScanInput;
        ScanInput = "";

        var normalized = BarcodeNormalizer.Normalize(raw);
        if (normalized is not BarcodeNormalizationResult.Valid valid)
        {
            LastMeasurementSummary = raw.Length == 0
                ? null
                : "Barkod okunamadı — okuyucunun bağlı olduğundan ve doğru barkodun okutulduğundan emin olun, ya da barkodu elle yazıp Enter'a basın.";
            return;
        }
        if (!_debouncer.ShouldProcess(valid.Barcode)) return;

        var lookup = await _engine.ScanAndLookupAsync(valid.Barcode, CancellationToken.None);
        if (lookup is not LookupOutcome.Found found)
        {
            CurrentOrder = null;
            LastMeasurementSummary = DescribeLookupFailure(lookup);
            return;
        }

        CurrentOrder = found.Order;

        var measureResult = await _engine.MeasureAndSubmitAsync(found.Order, CancellationToken.None);
        LastMeasurementSummary = Describe(measureResult);
        await RefreshQueueCountAsync();
    }

    private static string DescribeLookupFailure(LookupOutcome outcome) => outcome switch
    {
        LookupOutcome.InvalidBarcode i => i.Reason,
        LookupOutcome.NotFound n => n.Message,
        LookupOutcome.Offline => "Sunucuya ulaşılamadı — bağlantınızı kontrol edin.",
        LookupOutcome.Unauthorized => "İstasyon yetkisiz — Ayarlar'dan istasyon anahtarını kontrol edin.",
        LookupOutcome.StationDisabled => "İstasyon devre dışı bırakılmış.",
        _ => "Bilinmeyen bir hata oluştu.",
    };

    private static string Describe(MeasureOutcome outcome) => outcome switch
    {
        MeasureOutcome.Pass p => $"UYGUN — Ölçüm: {p.MeasurementId}",
        MeasureOutcome.Mismatch m => $"UYUMSUZ — Ölçüm: {m.MeasurementId} (Kanıt yüklendi: {(m.EvidenceUploaded ? "Evet" : "Hayır, kuyrukta")})",
        MeasureOutcome.QueuedOffline q => $"Çevrimdışı: {q.Reason}",
        MeasureOutcome.EasyCubeFailure f => $"EasyCube cihazı bulunamadı veya yanıt vermiyor: {f.Reason}",
        MeasureOutcome.CorrelationRejected c => $"Doğrulama reddedildi: {c.Reason}",
        MeasureOutcome.Rejected r => $"Reddedildi: {r.Reason}",
        MeasureOutcome.LookupFailed l => $"Gönderi bulunamadı: {DescribeLookupFailure(l.Reason)}",
        _ => "Bilinmeyen sonuç",
    };

    /// <summary>Builds and writes the sanitized diagnostics report — see DiagnosticsReportBuilder for exactly what it does and does not contain.</summary>
    public async Task ExportDiagnosticsAsync(string filePath)
    {
        var report = await DiagnosticsReportBuilder.BuildAsync(
            AgentVersion,
            _settings.GbexApiBaseUrl,
            StationStatusText,
            LastHeartbeatAt,
            _secretStore,
            $"{_settings.EasyCubeTcpHost}:{_settings.EasyCubeTcpPort}",
            EasyCubeStatusText,
            _settings.EasyCubeBaseUrl,
            EasyCubeFallbackStatusText,
            EasyCubeDeviceModel,
            EasyCubeSoftwareVersion,
            _settings.DeviceId,
            _outbox,
            CancellationToken.None);

        await File.WriteAllTextAsync(filePath, DiagnosticsReportBuilder.RenderAsText(report));
    }

    public void Dispose()
    {
        _queueCountTimer.Stop();
        _easyCubeHealthTimer.Stop();
        _engine.StateChanged -= OnEngineStateChanged;
        _heartbeat.StateChanged -= OnHeartbeatStateChanged;
        _easyCubeConnection.StateChanged -= OnEasyCubeConnectionStateChanged;
        _easyCubeConnection.MeasurementReceived -= OnDeviceMeasurementReceivedAsync;
    }
}
