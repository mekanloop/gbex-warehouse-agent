using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Gbex.Warehouse.Agent.Core.Abstractions;

namespace Gbex.Warehouse.Agent.Windows.Secrets;

/// <summary>
/// Windows DPAPI-backed station secret storage — the ONLY real
/// implementation of ISecretStore used in production. Encrypts with
/// ProtectedData.Protect scoped to the CURRENT USER (not machine-wide), so
/// the ciphertext on disk is useless to anything other than the exact
/// Windows account the Agent runs under. The plaintext secret is NEVER
/// written to JSON, SQLite, logs, or any other file — only this one DPAPI
/// blob. "Remove station credential" simply deletes the blob file.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _filePath;
    private static readonly byte[] Entropy = "gbex-warehouse-agent-station-secret-v1"u8.ToArray();

    public DpapiSecretStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "station.secret");
    }

    public async Task SaveStationSecretAsync(string secret, CancellationToken ct)
    {
        var plaintext = System.Text.Encoding.UTF8.GetBytes(secret);
        var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_filePath, protectedBytes, ct);
    }

    public async Task<string?> TryGetStationSecretAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return null;

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_filePath, ct);
            var plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            // Blob is corrupt or was encrypted under a different user profile
            // (e.g. the OS user account changed) — treat as "no secret",
            // never surface the raw decryption failure to the operator.
            return null;
        }
    }

    public Task RemoveStationSecretAsync(CancellationToken ct)
    {
        if (File.Exists(_filePath)) File.Delete(_filePath);
        return Task.CompletedTask;
    }

    public Task<bool> HasStationSecretAsync(CancellationToken ct) => Task.FromResult(File.Exists(_filePath));
}
