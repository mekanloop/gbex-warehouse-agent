using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Barcode;
using Gbex.Warehouse.Agent.Core.Models;
using Gbex.Warehouse.Agent.Core.Workflow;
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
    private readonly ScanDebouncer _debouncer;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _queueCountTimer;

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

    public MainViewModel(WarehouseWorkflowEngine engine, HeartbeatService heartbeat, IOutboxStore outbox, IClock clock, string agentVersion)
    {
        _engine = engine;
        _heartbeat = heartbeat;
        _outbox = outbox;
        _debouncer = new ScanDebouncer(clock);
        AgentVersion = agentVersion;
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _engine.StateChanged += OnEngineStateChanged;
        _heartbeat.StateChanged += OnHeartbeatStateChanged;

        _queueCountTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _queueCountTimer.Tick += async (_, _) => await RefreshQueueCountAsync();
        _queueCountTimer.Start();
    }

    private void OnEngineStateChanged(AgentWorkflowState state) =>
        _dispatcher.Invoke(() => WorkflowState = state);

    private void OnHeartbeatStateChanged(StationConnectionState _) =>
        _dispatcher.Invoke(() => { Raise(nameof(StationStatusText)); Raise(nameof(LastHeartbeatAt)); });

    private async Task RefreshQueueCountAsync()
    {
        var count = await _outbox.CountPendingAsync(CancellationToken.None);
        OfflineQueueCount = count;
    }

    /// <summary>Called from the scan textbox's Enter-key handler — the ONLY entry point that starts a new lookup/measure cycle.</summary>
    public async Task OnBarcodeScannedAsync()
    {
        var raw = ScanInput;
        ScanInput = "";

        var normalized = BarcodeNormalizer.Normalize(raw);
        if (normalized is not BarcodeNormalizationResult.Valid valid) return;
        if (!_debouncer.ShouldProcess(valid.Barcode)) return;

        var lookup = await _engine.ScanAndLookupAsync(valid.Barcode, CancellationToken.None);
        if (lookup is not LookupOutcome.Found found)
        {
            CurrentOrder = null;
            return;
        }

        CurrentOrder = found.Order;

        var measureResult = await _engine.MeasureAndSubmitAsync(found.Order, CancellationToken.None);
        LastMeasurementSummary = Describe(measureResult);
        await RefreshQueueCountAsync();
    }

    private static string Describe(MeasureOutcome outcome) => outcome switch
    {
        MeasureOutcome.Pass p => $"UYGUN — Ölçüm: {p.MeasurementId}",
        MeasureOutcome.Mismatch m => $"UYUMSUZ — Ölçüm: {m.MeasurementId} (Kanıt yüklendi: {(m.EvidenceUploaded ? "Evet" : "Hayır, kuyrukta")})",
        MeasureOutcome.QueuedOffline q => $"Çevrimdışı: {q.Reason}",
        MeasureOutcome.EasyCubeFailure f => $"Cihaz hatası: {f.Reason}",
        MeasureOutcome.CorrelationRejected c => $"Doğrulama reddedildi: {c.Reason}",
        MeasureOutcome.Rejected r => $"Reddedildi: {r.Reason}",
        _ => "Bilinmeyen sonuç",
    };

    public void Dispose()
    {
        _queueCountTimer.Stop();
        _engine.StateChanged -= OnEngineStateChanged;
        _heartbeat.StateChanged -= OnHeartbeatStateChanged;
    }
}
