using System.Globalization;

namespace Gbex.Warehouse.Agent.Core.Units;

public abstract record UnitParseResult
{
    public sealed record Ok(decimal Value) : UnitParseResult;
    public sealed record InvalidNumber(string RawValue) : UnitParseResult;
    public sealed record UnrecognizedUnit(string Unit) : UnitParseResult;
    public sealed record OutOfRange(decimal Value, decimal Min, decimal Max) : UnitParseResult;
}

/// <summary>
/// Normalizes EasyCube's raw numeric+unit pairs to GBEX's expected KG/CM,
/// using invariant-culture parsing throughout — a Windows PC's regional
/// settings (comma decimal separators, different thousands separators) must
/// never change how a measurement is interpreted. Every device value passes
/// through here before it reaches Core's CapturedMeasurement model.
/// </summary>
public static class UnitConverter
{
    // Generous but real bounds — matches the backend's own sanity check
    // (weightKg/lengthCm/widthCm/heightCm must be >0 and <=1000/500/500/500)
    // so an obviously-broken device reading is rejected here rather than
    // being submitted and failing remotely.
    public const decimal MaxWeightKg = 1000m;
    public const decimal MaxLengthCm = 500m;

    public static UnitParseResult ParseWeightToKg(decimal rawValue, string? unit)
    {
        var normalizedUnit = (unit ?? "kg").Trim().ToLowerInvariant();
        decimal kg = normalizedUnit switch
        {
            "kg" => rawValue,
            "g" or "gr" or "gram" or "grams" => rawValue / 1000m,
            "lb" or "lbs" or "pound" or "pounds" => rawValue * 0.45359237m,
            _ => decimal.MinValue,
        };

        if (kg == decimal.MinValue)
        {
            return new UnitParseResult.UnrecognizedUnit(unit ?? "(null)");
        }

        return Validate(kg, 0m, MaxWeightKg);
    }

    public static UnitParseResult ParseLengthToCm(decimal rawValue, string? unit)
    {
        var normalizedUnit = (unit ?? "cm").Trim().ToLowerInvariant();
        decimal cm = normalizedUnit switch
        {
            "cm" => rawValue,
            "mm" => rawValue / 10m,
            "m" or "meter" or "meters" => rawValue * 100m,
            "in" or "inch" or "inches" => rawValue * 2.54m,
            _ => decimal.MinValue,
        };

        if (cm == decimal.MinValue)
        {
            return new UnitParseResult.UnrecognizedUnit(unit ?? "(null)");
        }

        return Validate(cm, 0m, MaxLengthCm);
    }

    private static UnitParseResult Validate(decimal value, decimal exclusiveMin, decimal inclusiveMax)
    {
        if (value <= exclusiveMin || value > inclusiveMax)
        {
            return new UnitParseResult.OutOfRange(value, exclusiveMin, inclusiveMax);
        }

        return new UnitParseResult.Ok(value);
    }

    /// <summary>Invariant-culture decimal parse — the ONLY way a numeric string from the device or the WPF UI should ever be parsed in this codebase.</summary>
    public static UnitParseResult ParseInvariantDecimal(string rawValue)
    {
        if (decimal.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return new UnitParseResult.Ok(value);
        }

        return new UnitParseResult.InvalidNumber(rawValue);
    }
}
