namespace Gbex.Warehouse.Agent.Core.Abstractions;

/// <summary>
/// Abstraction over station-secret storage so tests (and any non-Windows
/// build of Core/Infrastructure) can use an in-memory fake. The real Windows
/// implementation (DpapiSecretStore, in the Windows project) encrypts with
/// DPAPI/Credential Manager — it is never referenced from Core or
/// Infrastructure, only composed in at the Windows project's DI root.
/// </summary>
public interface ISecretStore
{
    Task SaveStationSecretAsync(string secret, CancellationToken ct);
    Task<string?> TryGetStationSecretAsync(CancellationToken ct);
    Task RemoveStationSecretAsync(CancellationToken ct);
    Task<bool> HasStationSecretAsync(CancellationToken ct);
}
