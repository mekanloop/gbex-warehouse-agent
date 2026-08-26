using Gbex.Warehouse.Agent.Core.Abstractions;

namespace Gbex.Warehouse.Agent.Infrastructure.Secrets;

/// <summary>
/// In-memory fake secret store — used by tests and any non-Windows run of
/// the Agent (e.g. the integration test suite, which runs on whatever CI/dev
/// platform is available). Never used in the real Windows build; the
/// Windows project supplies DpapiSecretStore instead. Deliberately holds the
/// secret only in a process-local field — never written to disk in any form.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private string? _secret;

    public Task SaveStationSecretAsync(string secret, CancellationToken ct)
    {
        _secret = secret;
        return Task.CompletedTask;
    }

    public Task<string?> TryGetStationSecretAsync(CancellationToken ct) => Task.FromResult(_secret);

    public Task RemoveStationSecretAsync(CancellationToken ct)
    {
        _secret = null;
        return Task.CompletedTask;
    }

    public Task<bool> HasStationSecretAsync(CancellationToken ct) => Task.FromResult(_secret is not null);
}
