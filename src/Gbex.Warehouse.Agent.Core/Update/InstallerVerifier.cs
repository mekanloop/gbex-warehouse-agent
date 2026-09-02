using System.Security.Cryptography;

namespace Gbex.Warehouse.Agent.Core.Update;

/// <summary>
/// Pure, I/O-free verification logic for a downloaded update — kept in Core
/// (not Infrastructure) specifically so it is unit-testable without a real
/// file or network call. A downloaded installer must NEVER be executed
/// before this passes: a partial/corrupted download or a tampered response
/// must be caught here, not discovered by Windows failing to run a bad exe.
/// </summary>
public static class InstallerVerifier
{
    /// <summary>Case-insensitive, whitespace-tolerant hex comparison — servers and manifests are not guaranteed to agree on casing.</summary>
    public static bool VerifySha256(byte[] fileBytes, string expectedHexSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedHexSha256)) return false;
        // Convert.ToHexStringLower is a .NET 9+ API — this project targets
        // net8.0/net8.0-windows, so lowercase explicitly instead.
        var actual = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
        return string.Equals(actual, expectedHexSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True only when `candidateVersion` is a strictly higher, well-formed
    /// version than `currentVersion`. Deliberately conservative: if EITHER
    /// string fails to parse, returns false rather than guessing — an
    /// unparseable manifest value must never be treated as "obviously
    /// newer, install it", since that could turn a malformed/misconfigured
    /// server response into a forced update loop.
    /// </summary>
    public static bool IsNewerVersion(string candidateVersion, string currentVersion)
    {
        if (!Version.TryParse(candidateVersion, out var candidate)) return false;
        if (!Version.TryParse(currentVersion, out var current)) return false;
        return candidate > current;
    }
}
