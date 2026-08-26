namespace Gbex.Warehouse.Agent.Core.Abstractions;

/// <summary>
/// Temporary evidence image handling. A captured image is held ONLY until
/// the backend returns PASS or MISMATCH: PASS deletes it immediately,
/// MISMATCH uploads it once and deletes it after confirmed upload. Images
/// are never stored permanently outside the GBEX backend. The real
/// implementation (Infrastructure) uses restrictive file permissions and
/// unpredictable filenames, and purges abandoned files past a documented
/// retention limit.
/// </summary>
public interface IEvidenceImageStore
{
    /// <summary>Validates (MIME/format/size) and writes the image to a temporary location, returning an opaque handle (a local file path) — never the raw bytes back out.</summary>
    Task<string> SaveTemporaryAsync(byte[] imageBytes, string mimeType, CancellationToken ct);

    Task<byte[]> ReadAsync(string handle, CancellationToken ct);

    Task DeleteAsync(string handle, CancellationToken ct);

    /// <summary>Deletes any temporary evidence file older than the retention limit that was never cleaned up (e.g. after a crash) — run periodically, not just on the PASS/MISMATCH paths.</summary>
    Task<int> PurgeAbandonedAsync(TimeSpan retentionLimit, CancellationToken ct);
}
