using System.IO;
using System.Reflection;
using System.Windows;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Correlation;
using Gbex.Warehouse.Agent.Core.Idempotency;
using Gbex.Warehouse.Agent.Core.Workflow;
using Gbex.Warehouse.Agent.Infrastructure.EasyCube;
using Gbex.Warehouse.Agent.Infrastructure.Evidence;
using Gbex.Warehouse.Agent.Infrastructure.Gbex;
using Gbex.Warehouse.Agent.Infrastructure.Heartbeat;
using Gbex.Warehouse.Agent.Infrastructure.Outbox;
using Gbex.Warehouse.Agent.Windows.Printing;
using Gbex.Warehouse.Agent.Windows.Secrets;
using Gbex.Warehouse.Agent.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gbex.Warehouse.Agent.Windows;

/// <summary>
/// Composition root. This is the ONLY place DpapiSecretStore is ever
/// constructed — Core and Infrastructure never reference it, keeping the
/// hardware/workflow layers testable on any platform. All app data
/// (settings, station secret blob, SQLite outbox, temporary evidence
/// images, logs) lives under %LOCALAPPDATA%\GbexWarehouseAgent.
/// </summary>
public partial class App : Application
{
    public static string AgentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GbexWarehouseAgent");
        Directory.CreateDirectory(appDataDir);

        var settingsStore = new AgentSettingsStore(appDataDir);
        var settings = settingsStore.Load();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddDebug();
        // File-based rotating logs are configured via the standard
        // Microsoft.Extensions.Logging providers — see docs/DEPLOYMENT.md
        // for the log location and rotation/retention policy. Never logs
        // secrets (see GbexApiClient/EasyCubeClient — they only log status
        // codes and error type names).

        builder.Services.AddSingleton(settingsStore);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<ISecretStore>(new DpapiSecretStore(appDataDir));
        builder.Services.AddSingleton<IIdempotencyKeyGenerator, GuidIdempotencyKeyGenerator>();
        builder.Services.AddSingleton(sp => new MeasurementCorrelationValidator(sp.GetRequiredService<IClock>()));
        builder.Services.AddSingleton<IEvidenceImageStore>(sp =>
            new TemporaryImageStore(Path.Combine(appDataDir, "evidence-temp"), sp.GetRequiredService<ILogger<TemporaryImageStore>>()));
        builder.Services.AddSingleton<IOutboxStore>(sp =>
            new SqliteOutboxStore(Path.Combine(appDataDir, "outbox.db"), sp.GetRequiredService<IClock>(), sp.GetRequiredService<ILogger<SqliteOutboxStore>>()));
        builder.Services.AddSingleton<ILabelPrinter, FakeLabelPrinter>();

        // GbexApiClient's own constructor sets BaseAddress/Timeout from
        // GbexApiOptions — no HttpClient configuration needed here.
        // GbexApiOptions/EasyCubeOptions use init-only properties (by
        // design — they're meant to be constructed once, not mutated), so
        // they're built directly here rather than via Configure<T>(Action<T>),
        // which requires a mutable target.
        builder.Services.AddHttpClient<IGbexApiClient, GbexApiClient>();
        builder.Services.AddSingleton(Options.Create(new GbexApiOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(settings.GbexApiBaseUrl) ? "https://app.gbex.com.tr" : settings.GbexApiBaseUrl,
            AllowInsecureForDevelopment = settings.AllowInsecureGbexForDevelopment,
        }));

        builder.Services.AddHttpClient<IEasyCubeClient, EasyCubeClient>();
        builder.Services.AddSingleton(Options.Create(new EasyCubeOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(settings.EasyCubeBaseUrl) ? "http://localhost:8080" : settings.EasyCubeBaseUrl,
        }));

        builder.Services.AddSingleton(sp => new WarehouseWorkflowEngine(
            sp.GetRequiredService<IGbexApiClient>(),
            sp.GetRequiredService<IEasyCubeClient>(),
            sp.GetRequiredService<IEvidenceImageStore>(),
            sp.GetRequiredService<IOutboxStore>(),
            sp.GetRequiredService<IIdempotencyKeyGenerator>(),
            sp.GetRequiredService<MeasurementCorrelationValidator>(),
            sp.GetRequiredService<ILogger<WarehouseWorkflowEngine>>()));

        builder.Services.AddSingleton(sp => new HeartbeatService(
            sp.GetRequiredService<IGbexApiClient>(),
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<ILogger<HeartbeatService>>(),
            AgentVersion));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HeartbeatService>());
        builder.Services.AddHostedService<OutboxProcessor>();

        builder.Services.AddSingleton<MainViewModel>(sp => new MainViewModel(
            sp.GetRequiredService<WarehouseWorkflowEngine>(),
            sp.GetRequiredService<HeartbeatService>(),
            sp.GetRequiredService<IOutboxStore>(),
            sp.GetRequiredService<IClock>(),
            AgentVersion));
        builder.Services.AddTransient<StationSettingsWindow>();
        builder.Services.AddSingleton<MainWindow>(sp => new MainWindow(
            sp.GetRequiredService<MainViewModel>(),
            () => sp.GetRequiredService<StationSettingsWindow>()));

        _host = builder.Build();
        _host.Start();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        _host?.Dispose();
        base.OnExit(e);
    }
}
