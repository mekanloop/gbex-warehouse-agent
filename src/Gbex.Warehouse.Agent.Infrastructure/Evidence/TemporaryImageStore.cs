using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Gbex.Warehouse.Agent.Infrastructure.Evidence;

/// <summary>
/// Temporary evidence image handling. Mirrors the gbex website backend's own
/// validation exactly (lib/file-validation.ts: 8MB cap, JPEG/PNG/WEBP magic-
/// byte sniff cross-checked against the declared MIME type) so an image that
/// would be rejected server-side is caught here first. Filenames are
/// cryptographically random (never derived from the barcode or any
/// predictable value); the containing directory is created with the most
/// restrictive permissions this platform supports. Never logs image bytes.
/// </summary>
public sealed class TemporaryImageStore : IEvidenceImageStore
{
    public const int MaxImageBytes = 8 * 1024 * 1024;

    private readonly string _directory;
    private readonly ILogger<TemporaryImageStore> _logger;

    public TemporaryImageStore(string directory, ILogger<TemporaryImageStore> logger)
    {
        _directory = directory;
        _logger = logger;
        Directory.CreateDirectory(_directory);
        TryRestrictDirectoryPermissions();
    }

    public async Task<string> SaveTemporaryAsync(byte[] imageBytes, string mimeType, CancellationToken ct)
    {
        if (imageBytes.Length == 0 || imageBytes.Length > MaxImageBytes)
        {
            throw new InvalidOperationException($"Evidence image size {imageBytes.Length} bytes is outside the allowed range (1..{MaxImageBytes}).");
        }

        var sniffed = SniffImageType(imageBytes);
        if (sniffed is null || !string.Equals(sniffed, mimeType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Evidence image content does not match its declared type or is not a recognized format.");
        }

        var extension = sniffed switch
        {
            "image/png" => "png",
            "image/webp" => "webp",
            _ => "jpg",
        };

        var fileName = $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}.{extension}";
        var path = Path.Combine(_directory, fileName);

        await File.WriteAllBytesAsync(path, imageBytes, ct);
        TryRestrictFilePermissions(path);

        // Never log the byte content — only the fact that a file was written and its size.
        _logger.LogDebug("Saved temporary evidence image ({Size} bytes)", imageBytes.Length);
        return path;
    }

    public async Task<byte[]> ReadAsync(string handle, CancellationToken ct) => await File.ReadAllBytesAsync(handle, ct);

    public Task DeleteAsync(string handle, CancellationToken ct)
    {
        try
        {
            if (File.Exists(handle)) File.Delete(handle);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to delete temporary evidence file — will be swept by PurgeAbandonedAsync");
        }
        return Task.CompletedTask;
    }

    public Task<int> PurgeAbandonedAsync(TimeSpan retentionLimit, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - retentionLimit;
        var purged = 0;
        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc < cutoff)
            {
                try
                {
                    info.Delete();
                    purged++;
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Failed to purge abandoned evidence file");
                }
            }
        }
        return Task.FromResult(purged);
    }

    private static string? SniffImageType(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A) return "image/png";
        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P') return "image/webp";
        return null;
    }

    private void TryRestrictDirectoryPermissions()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (PlatformNotSupportedException) { }
        }
        // On Windows, the directory lives under the current user's LocalAppData
        // by construction (see Windows project composition root) — NTFS ACLs
        // already restrict it to that user account by default.
    }

    private void TryRestrictFilePermissions(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (PlatformNotSupportedException) { }
        }
    }
}
