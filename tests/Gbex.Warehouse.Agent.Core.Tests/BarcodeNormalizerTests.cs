using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Barcode;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

public class BarcodeNormalizerTests
{
    [Theory]
    [InlineData("GBEX2508230001", "GBEX2508230001")]
    [InlineData("  GBEX2508230001  ", "GBEX2508230001")]
    [InlineData("gbex2508230001", "GBEX2508230001")]
    [InlineData("GBEX2508230001\r\n", "GBEX2508230001")]
    [InlineData("GBX2508230001", "GBX2508230001")]
    [InlineData("gbx2508230001", "GBX2508230001")]
    [InlineData("  GBX2508230001  ", "GBX2508230001")]
    public void Normalize_accepts_and_uppercases_valid_barcodes(string input, string expected)
    {
        var result = BarcodeNormalizer.Normalize(input);
        var valid = Assert.IsType<BarcodeNormalizationResult.Valid>(result);
        Assert.Equal(expected, valid.Barcode);
    }

    [Fact]
    public void Normalize_rejects_empty_input()
    {
        Assert.IsType<BarcodeNormalizationResult.Empty>(BarcodeNormalizer.Normalize(""));
        Assert.IsType<BarcodeNormalizationResult.Empty>(BarcodeNormalizer.Normalize("   "));
        Assert.IsType<BarcodeNormalizationResult.Empty>(BarcodeNormalizer.Normalize(null));
    }

    [Fact]
    public void Normalize_rejects_excessively_long_input()
    {
        var result = BarcodeNormalizer.Normalize(new string('9', 100));
        Assert.IsType<BarcodeNormalizationResult.TooLong>(result);
    }

    [Theory]
    [InlineData("NOTGBEX1234567890")]
    [InlineData("GBEX123")]
    [InlineData("GBEX25082300012")]
    [InlineData("GBX123")]
    [InlineData("GBX25082300012")]
    [InlineData("<script>alert(1)</script>")]
    public void Normalize_rejects_wrong_format(string input)
    {
        Assert.IsType<BarcodeNormalizationResult.InvalidFormat>(BarcodeNormalizer.Normalize(input));
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    [Fact]
    public void ScanDebouncer_suppresses_a_rapid_duplicate_of_the_same_barcode()
    {
        var clock = new FakeClock();
        var debouncer = new ScanDebouncer(clock, TimeSpan.FromMilliseconds(500));

        Assert.True(debouncer.ShouldProcess("GBEX2508230001"));
        clock.UtcNow = clock.UtcNow.AddMilliseconds(100);
        Assert.False(debouncer.ShouldProcess("GBEX2508230001")); // bounced duplicate
    }

    [Fact]
    public void ScanDebouncer_allows_the_same_barcode_again_after_the_window_passes()
    {
        var clock = new FakeClock();
        var debouncer = new ScanDebouncer(clock, TimeSpan.FromMilliseconds(500));

        Assert.True(debouncer.ShouldProcess("GBEX2508230001"));
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        Assert.True(debouncer.ShouldProcess("GBEX2508230001"));
    }

    [Fact]
    public void ScanDebouncer_allows_a_different_barcode_immediately()
    {
        var clock = new FakeClock();
        var debouncer = new ScanDebouncer(clock, TimeSpan.FromMilliseconds(500));

        Assert.True(debouncer.ShouldProcess("GBEX2508230001"));
        Assert.True(debouncer.ShouldProcess("GBEX2508230002"));
    }
}
