using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Models;
using Gbex.Warehouse.Agent.Infrastructure.Heartbeat;
using Gbex.Warehouse.Agent.Infrastructure.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

public class HeartbeatServiceTests
{
    [Fact]
    public async Task Stays_offline_until_a_station_secret_is_configured()
    {
        var client = new Mock<IGbexApiClient>();
        var secretStore = new InMemorySecretStore(); // no secret saved
        var service = new HeartbeatService(client.Object, secretStore, NullLogger<HeartbeatService>.Instance, "1.0.0");

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(StationConnectionState.Offline, service.State);
        client.Verify(c => c.HeartbeatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reports_connected_after_a_successful_heartbeat()
    {
        var client = new Mock<IGbexApiClient>();
        client.Setup(c => c.HeartbeatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HeartbeatOutcome.Ok("TEST-STATION-06"));
        var secretStore = new InMemorySecretStore();
        await secretStore.SaveStationSecretAsync("wst_test", CancellationToken.None);

        var service = new HeartbeatService(client.Object, secretStore, NullLogger<HeartbeatService>.Instance, "1.0.0");
        var stateChanges = new List<StationConnectionState>();
        service.StateChanged += stateChanges.Add;

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(StationConnectionState.Connected, service.State);
        Assert.Contains(StationConnectionState.Connected, stateChanges);
        Assert.NotNull(service.LastSuccessfulHeartbeatAt);
    }

    [Fact]
    public async Task Reports_unauthorized_on_401_and_does_not_hammer_retries()
    {
        var client = new Mock<IGbexApiClient>();
        client.Setup(c => c.HeartbeatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GbexApiResult.Unauthorized());
        var secretStore = new InMemorySecretStore();
        await secretStore.SaveStationSecretAsync("wst_test", CancellationToken.None);

        var service = new HeartbeatService(client.Object, secretStore, NullLogger<HeartbeatService>.Instance, "1.0.0");

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(StationConnectionState.Unauthorized, service.State);
        // The unauthorized poll interval is minutes, not milliseconds — in
        // 150ms this must have been called only once, never hammered.
        client.Verify(c => c.HeartbeatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Reports_disabled_state_distinctly_from_unauthorized()
    {
        var client = new Mock<IGbexApiClient>();
        client.Setup(c => c.HeartbeatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GbexApiResult.StationDisabled());
        var secretStore = new InMemorySecretStore();
        await secretStore.SaveStationSecretAsync("wst_test", CancellationToken.None);

        var service = new HeartbeatService(client.Object, secretStore, NullLogger<HeartbeatService>.Instance, "1.0.0");

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(StationConnectionState.Disabled, service.State);
    }

    [Fact]
    public async Task Backs_off_after_repeated_transient_failures_before_declaring_offline()
    {
        var client = new Mock<IGbexApiClient>();
        client.Setup(c => c.HeartbeatAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GbexApiResult.TransientFailure("network"));
        var secretStore = new InMemorySecretStore();
        await secretStore.SaveStationSecretAsync("wst_test", CancellationToken.None);

        var service = new HeartbeatService(client.Object, secretStore, NullLogger<HeartbeatService>.Instance, "1.0.0");
        var stateChanges = new List<StationConnectionState>();
        service.StateChanged += stateChanges.Add;

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);

        Assert.Contains(StationConnectionState.Degraded, stateChanges);
    }
}
