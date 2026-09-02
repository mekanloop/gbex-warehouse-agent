using System.Security.Cryptography;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Infrastructure.Gbex;
using Gbex.Warehouse.Agent.Infrastructure.Secrets;
using Gbex.Warehouse.Agent.Infrastructure.Update;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gbex.Warehouse.Agent.IntegrationTests;

/// <summary>
/// Real GbexApiClient + real Kestrel-backed FakeGbexBackend + a real
/// AgentUpdateService on a real temp directory — the only fake is the
/// backend HTTP server itself, same posture as EndToEndWorkflowTests.
/// Exercises the actual self-update contract end-to-end, including the
/// verification step that must reject a corrupted/mismatched download
/// before it is ever considered installable.
/// </summary>
public class AgentUpdateTests : IAsyncLifetime
{
    private FakeGbexBackend _gbexBackend = null!;
    private string _tempDir = null!;

    public async Task InitializeAsync()
    {
        _gbexBackend = new FakeGbexBackend();
        await _gbexBackend.StartAsync();
        _tempDir = Path.Combine(Path.GetTempPath(), "gbex-agent-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public async Task DisposeAsync()
    {
        await _gbexBackend.DisposeAsync();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private GbexApiClient BuildClient(out ISecretStore secretStore)
    {
        secretStore = new InMemorySecretStore();
        secretStore.SaveStationSecretAsync(_gbexBackend.ValidToken, CancellationToken.None).GetAwaiter().GetResult();
        var options = Options.Create(new GbexApiOptions { BaseUrl = _gbexBackend.BaseUrl, AllowInsecureForDevelopment = true });
        return new GbexApiClient(new HttpClient(), options, secretStore, NullLogger<GbexApiClient>.Instance);
    }

    [Fact]
    public async Task CheckForUpdateAsync_reports_none_available_when_backend_has_no_release()
    {
        var client = BuildClient(out _);

        var result = await client.CheckForUpdateAsync(CancellationToken.None);

        var outcome = Assert.IsType<AgentUpdateCheckOutcome>(result);
        Assert.Null(outcome.Manifest);
    }

    [Fact]
    public async Task CheckForUpdateAsync_returns_the_published_manifest()
    {
        _gbexBackend.AgentReleaseVersion = "1.10.0";
        _gbexBackend.AgentReleaseSha256 = "deadbeef";
        _gbexBackend.AgentReleaseNotes = "bug fixes";
        _gbexBackend.AgentReleaseMandatory = true;
        var client = BuildClient(out _);

        var result = await client.CheckForUpdateAsync(CancellationToken.None);

        var outcome = Assert.IsType<AgentUpdateCheckOutcome>(result);
        Assert.NotNull(outcome.Manifest);
        Assert.Equal("1.10.0", outcome.Manifest!.LatestVersion);
        Assert.Equal("deadbeef", outcome.Manifest.Sha256);
        Assert.Equal("bug fixes", outcome.Manifest.ReleaseNotes);
        Assert.True(outcome.Manifest.Mandatory);
        Assert.Equal("/api/warehouse/agent-version/download", outcome.Manifest.InstallerUrl);
    }

    [Fact]
    public async Task DownloadUpdateInstallerAsync_writes_the_exact_bytes_the_backend_serves()
    {
        var installerBytes = new byte[] { 0x4D, 0x5A, 1, 2, 3, 4, 5 }; // "MZ" DOS header + filler
        _gbexBackend.AgentReleaseBytes = installerBytes;
        var client = BuildClient(out _);
        var destination = Path.Combine(_tempDir, "setup.exe");

        var result = await client.DownloadUpdateInstallerAsync("/api/warehouse/agent-version/download", destination, CancellationToken.None);

        Assert.IsType<GbexApiResult.Success>(result);
        Assert.Equal(installerBytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task AgentUpdateService_stages_a_newer_verified_release_as_pending()
    {
        var installerBytes = new byte[] { 0x4D, 0x5A, 9, 9, 9 };
        _gbexBackend.AgentReleaseVersion = "9.9.9";
        _gbexBackend.AgentReleaseSha256 = Convert.ToHexString(SHA256.HashData(installerBytes)).ToLowerInvariant();
        _gbexBackend.AgentReleaseBytes = installerBytes;

        var client = BuildClient(out var secretStore);
        var service = new AgentUpdateService(client, secretStore, "1.0.0", _tempDir, NullLogger<AgentUpdateService>.Instance);

        await InvokeCheckOnceAsync(service);

        Assert.NotNull(service.PendingUpdate);
        Assert.Equal("9.9.9", service.PendingUpdate!.Version);
        Assert.True(File.Exists(service.PendingUpdate.InstallerPath));
        Assert.Equal(installerBytes, await File.ReadAllBytesAsync(service.PendingUpdate.InstallerPath));
    }

    [Fact]
    public async Task AgentUpdateService_discards_a_release_whose_bytes_do_not_match_the_manifest_sha256()
    {
        var installerBytes = new byte[] { 0x4D, 0x5A, 9, 9, 9 };
        _gbexBackend.AgentReleaseVersion = "9.9.9";
        _gbexBackend.AgentReleaseSha256 = "0000000000000000000000000000000000000000000000000000000000000000"; // deliberately wrong
        _gbexBackend.AgentReleaseBytes = installerBytes;

        var client = BuildClient(out var secretStore);
        var service = new AgentUpdateService(client, secretStore, "1.0.0", _tempDir, NullLogger<AgentUpdateService>.Instance);

        await InvokeCheckOnceAsync(service);

        // Never staged, and never left sitting on disk as something that
        // could later be mistaken for a verified installer.
        Assert.Null(service.PendingUpdate);
        Assert.Empty(Directory.GetFiles(Path.Combine(_tempDir, "updates")));
    }

    [Fact]
    public async Task AgentUpdateService_does_nothing_when_the_published_version_is_not_newer()
    {
        _gbexBackend.AgentReleaseVersion = "1.0.0";
        _gbexBackend.AgentReleaseSha256 = "irrelevant";
        _gbexBackend.AgentReleaseBytes = new byte[] { 1 };

        var client = BuildClient(out var secretStore);
        var service = new AgentUpdateService(client, secretStore, "1.0.0", _tempDir, NullLogger<AgentUpdateService>.Instance);

        await InvokeCheckOnceAsync(service);

        Assert.Null(service.PendingUpdate);
    }

    /// <summary>
    /// AgentUpdateService's actual check logic is private (ExecuteAsync
    /// sleeps 30s before its first run, which would make every test using
    /// it slow) — reflection invokes the same CheckOnceAsync the background
    /// loop calls, rather than duplicating its logic or exposing it as
    /// public API purely for tests to reach.
    /// </summary>
    private static async Task InvokeCheckOnceAsync(AgentUpdateService service)
    {
        var method = typeof(AgentUpdateService).GetMethod("CheckOnceAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(service, new object[] { CancellationToken.None })!;
    }
}
