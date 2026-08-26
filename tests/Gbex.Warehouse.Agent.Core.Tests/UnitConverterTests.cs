using System.Globalization;
using System.Threading;
using Gbex.Warehouse.Agent.Core.Units;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

public class UnitConverterTests
{
    [Theory]
    [InlineData(5.0, "kg", 5.0)]
    [InlineData(5000, "g", 5.0)]
    [InlineData(1, "lb", 0.45359237)]
    public void ParseWeightToKg_converts_known_units(double raw, string unit, double expectedKg)
    {
        var result = UnitConverter.ParseWeightToKg((decimal)raw, unit);
        var ok = Assert.IsType<UnitParseResult.Ok>(result);
        Assert.Equal((decimal)expectedKg, ok.Value, 6);
    }

    [Fact]
    public void ParseWeightToKg_rejects_unrecognized_unit()
    {
        var result = UnitConverter.ParseWeightToKg(5, "cm3");
        Assert.IsType<UnitParseResult.UnrecognizedUnit>(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1001)]
    public void ParseWeightToKg_rejects_out_of_range(double raw)
    {
        var result = UnitConverter.ParseWeightToKg((decimal)raw, "kg");
        Assert.IsType<UnitParseResult.OutOfRange>(result);
    }

    [Theory]
    [InlineData(40, "cm", 40)]
    [InlineData(400, "mm", 40)]
    [InlineData(1, "in", 2.54)]
    public void ParseLengthToCm_converts_known_units(double raw, string unit, double expectedCm)
    {
        var result = UnitConverter.ParseLengthToCm((decimal)raw, unit);
        var ok = Assert.IsType<UnitParseResult.Ok>(result);
        Assert.Equal((decimal)expectedCm, ok.Value, 6);
    }

    [Fact]
    public void ParseInvariantDecimal_ignores_current_culture_comma_decimal_separator()
    {
        // Simulate a Windows machine with a Turkish/German-style regional
        // setting (comma as the decimal separator) — parsing must still use
        // invariant culture, not CurrentCulture, or "5.5" would misparse.
        var originalCulture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");

            var result = UnitConverter.ParseInvariantDecimal("5.5");
            var ok = Assert.IsType<UnitParseResult.Ok>(result);
            Assert.Equal(5.5m, ok.Value);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void ParseInvariantDecimal_rejects_a_comma_decimal_separator_regardless_of_locale()
    {
        // Invariant culture uses '.' — a device or config value using ','
        // must be rejected rather than silently misinterpreted as a
        // thousands separator.
        var result = UnitConverter.ParseInvariantDecimal("5,5");
        Assert.IsType<UnitParseResult.InvalidNumber>(result);
    }
}
