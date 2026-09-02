using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Update;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gbex.Warehouse.Agent.Infrastructure.Update;

public sealed record PendingAgentUpdate(string Version, string? ReleaseNotes, bool Mandatory, string InstallerPath);

/// <summary>
/// Background self-update check for the Windows Agent. Runs entirely
/// independently of the measurement workflow — a network failure, an
/// unreachable server, or "no release published yet" are all treated as
/// ordinary, non-error outcomes that simply mean "nothing to do this time",
/// never something that blocks the scanner/measurement flow or crashes the
/// Agent. Checks once ~30s after startup (so first paint isn't delayed),
/// then on a fixed interval.
///
/// A staged update is downloaded to a per-version file (never overwriting a
/// previous good download mid-verification) and its SHA-256 is checked
/// against the manifest BEFORE it is ever considered "pending" — a failed
/// verification discards the file and is logged, never executed. Actually
/// running the verified installer only ever happens in one of two
/// deliberate places, both outside this class: App.xaml.cs's OnExit (silent,
/// on the NEXT normal close — never forced) or an explicit operator
/// "Update Now" click surfaced via UpdateReady/PendingUpdate for the UI to
/// bind to. This class never launches a process itself.
/// </summary>
public sealed class AgentUpdateService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly IGbexApiClient _gbexClient;
    private readonly ISecretStore _secretStore;
    private readonly string _agentVersion;
    private readonly string _updateDirectory;
    private readonly ILogger<AgentUpdateService> _logger;

    /// <summary>Set once a download has been verified against its manifest sha256 — null otherwise. Safe to read from any thread; only ever written here.</summary>
    public PendingAgentUpdate? PendingUpdate { get; private set; }
    public event Action<PendingAgentUpdate>? UpdateReady;

    public AgentUpdateService(
        IGbexApiClient gbexClient,
        ISecretStore secretStore,
        string agentVersion,
        string appDataDir,
        ILogger<AgentUpdateService> logger)
    {
        _gbexClient = gbexClient;
        _secretStore = secretStore;
        _agentVersion = agentVersion;
        _updateDirectory = Path.Combine(appDataDir, "updates");
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SafeDelay(InitialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never let an unexpected failure here take down the whole
                // Agent — the update check is strictly best-effort.
                _logger.LogWarning(ex, "Unexpected error during update check — will retry next interval");
            }

            await SafeDelay(CheckInterval, stoppingToken);
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        // Already have a verified, staged installer — nothing more to do
        // until it's applied (at next exit or an explicit "Update Now").
        if (PendingUpdate is not null) return;

        if (!await _secretStore.HasStationSecretAsync(ct)) return;

        var result = await _gbexClient.CheckForUpdateAsync(ct);
        if (result is not AgentUpdateCheckOutcome { Manifest: { } manifest })
        {
            // Unreachable, unauthorized, malformed, or genuinely nothing
            // published — all the same from here: quietly try again later.
            return;
        }

        if (!InstallerVerifier.IsNewerVersion(manifest.LatestVersion, _agentVersion)) return;

        Directory.CreateDirectory(_updateDirectory);
        var installerPath = Path.Combine(_updateDirectory, $"GbexWarehouseAgentSetup-{manifest.LatestVersion}.exe");

        var download = await _gbexClient.DownloadUpdateInstallerAsync(manifest.InstallerUrl, installerPath, ct);
        if (download is not GbexApiResult.Success)
        {
            _logger.LogWarning("Update download for {Version} did not succeed ({Result}) — will retry next interval", manifest.LatestVersion, download.GetType().Name);
            TryDelete(installerPath);
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(installerPath, ct);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not re-read downloaded update {Version} for verification", manifest.LatestVersion);
            TryDelete(installerPath);
            return;
        }

        if (!InstallerVerifier.VerifySha256(bytes, manifest.Sha256))
        {
            _logger.LogWarning("Downloaded update {Version} FAILED sha256 verification — discarding, never executing it", manifest.LatestVersion);
            TryDelete(installerPath);
            return;
        }

        var pending = new PendingAgentUpdate(manifest.LatestVersion, manifest.ReleaseNotes, manifest.Mandatory, installerPath);
        PendingUpdate = pending;
        _logger.LogInformation("Update {Version} downloaded and verified — staged for install", manifest.LatestVersion);
        UpdateReady?.Invoke(pending);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup only — a leftover temp file is harmless
            // and will be overwritten by the next successful download.
        }
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }
}
