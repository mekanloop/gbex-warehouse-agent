using Gbex.Warehouse.Agent.Infrastructure.Evidence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

public class TemporaryImageStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "gbex-agent-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly byte[] ValidJpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

    private TemporaryImageStore CreateStore() => new(_tempDir, NullLogger<TemporaryImageStore>.Instance);

    [Fact]
    public async Task SaveTemporaryAsync_uses_an_unpredictable_filename()
    {
        var store = CreateStore();
        var handle1 = await store.SaveTemporaryAsync(ValidJpegBytes, "image/jpeg", CancellationToken.None);
        var handle2 = await store.SaveTemporaryAsync(ValidJpegBytes, "image/jpeg", CancellationToken.None);

        Assert.NotEqual(Path.GetFileName(handle1), Path.GetFileName(handle2));
        // Not derived from anything predictable like a sequence or timestamp alone.
        Assert.True(Path.GetFileNameWithoutExtension(handle1).Length >= 32);
    }

    [Fact]
    public async Task SaveTemporaryAsync_rejects_a_declared_type_that_does_not_match_the_actual_bytes()
    {
        var store = CreateStore();
        var notActuallyPng = ValidJpegBytes; // real JPEG bytes, declared as PNG

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveTemporaryAsync(notActuallyPng, "image/png", CancellationToken.None));
    }

    [Fact]
    public async Task SaveTemporaryAsync_rejects_oversized_input()
    {
        var store = CreateStore();
        var tooBig = new byte[TemporaryImageStore.MaxImageBytes + 1];
        Array.Copy(ValidJpegBytes, tooBig, ValidJpegBytes.Length);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveTemporaryAsync(tooBig, "image/jpeg", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_removes_the_file()
    {
        var store = CreateStore();
        var handle = await store.SaveTemporaryAsync(ValidJpegBytes, "image/jpeg", CancellationToken.None);
        Assert.True(File.Exists(handle));

        await store.DeleteAsync(handle, CancellationToken.None);

        Assert.False(File.Exists(handle));
    }

    [Fact]
    public async Task PurgeAbandonedAsync_deletes_files_older_than_the_retention_limit_but_keeps_recent_ones()
    {
        var store = CreateStore();
        var recent = await store.SaveTemporaryAsync(ValidJpegBytes, "image/jpeg", CancellationToken.None);
        var old = await store.SaveTemporaryAsync(ValidJpegBytes, "image/jpeg", CancellationToken.None);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));

        var purged = await store.PurgeAbandonedAsync(TimeSpan.FromHours(1), CancellationToken.None);

        Assert.Equal(1, purged);
        Assert.True(File.Exists(recent));
        Assert.False(File.Exists(old));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
