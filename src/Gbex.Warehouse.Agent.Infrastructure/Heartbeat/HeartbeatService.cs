using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Models;
using Gbex.Warehouse.Agent.Infrastructure.Retry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gbex.Warehouse.Agent.Infrastructure.Heartbeat;

/// <summary>
/// Background heartbeat — begins only once a station secret is configured,
/// reports the current Agent version, and exposes the connection state via
/// StateChanged for the UI to bind to (never blocks the UI thread: this
/// entire service runs on a background Task via BackgroundService).
/// Exponential backoff+jitter on failure; 401 stops aggressive retry (falls
/// back to a long fixed interval — cheap enough to keep polling in case an
/// admin re-enables the station, but not hammering it every few seconds).
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    private static readonly TimeSpan HealthyInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UnauthorizedPollInterval = TimeSpan.FromMinutes(2);

    private readonly IGbexApiClient _gbexClient;
    private readonly ISecretStore _secretStore;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly string _agentVersion;
    private readonly Random _random = new();

    public StationConnectionState State { get; private set; } = StationConnectionState.Offline;
    public DateTimeOffset? LastSuccessfulHeartbeatAt { get; private set; }
    public event Action<StationConnectionState>? StateChanged;

    public HeartbeatService(IGbexApiClient gbexClient, ISecretStore secretStore, ILogger<HeartbeatService> logger, string agentVersion)
    {
        _gbexClient = gbexClient;
        _secretStore = secretStore;
        _logger = logger;
        _agentVersion = agentVersion;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var hasSecret = await _secretStore.HasStationSecretAsync(stoppingToken);
            if (!hasSecret)
            {
                SetState(StationConnectionState.Offline);
                await SafeDelay(HealthyInterval, stoppingToken);
                continue;
            }

            var result = await _gbexClient.HeartbeatAsync(_agentVersion, stoppingToken);

            switch (result)
            {
                case HeartbeatOutcome:
                    consecutiveFailures = 0;
                    LastSuccessfulHeartbeatAt = DateTimeOffset.UtcNow;
                    SetState(StationConnectionState.Connected);
                    await SafeDelay(HealthyInterval, stoppingToken);
                    break;

                case GbexApiResult.Unauthorized:
                    // Never retry aggressively on 401 — the token is
                    // revoked/invalid until an operator reconfigures it.
                    SetState(StationConnectionState.Unauthorized);
                    await SafeDelay(UnauthorizedPollInterval, stoppingToken);
                    break;

                case GbexApiResult.StationDisabled:
                    SetState(StationConnectionState.Disabled);
                    await SafeDelay(UnauthorizedPollInterval, stoppingToken);
                    break;

                default:
                    consecutiveFailures++;
                    SetState(consecutiveFailures >= 3 ? StationConnectionState.Offline : StationConnectionState.Degraded);
                    var backoff = BackoffPolicy.Compute(consecutiveFailures, BaseBackoff, MaxBackoff, _random);
                    _logger.LogWarning("Heartbeat failed ({Failures} consecutive) — retrying in {Backoff}", consecutiveFailures, backoff);
                    await SafeDelay(backoff, stoppingToken);
                    break;
            }
        }
    }

    private void SetState(StationConnectionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutting down — expected.
        }
    }
}
