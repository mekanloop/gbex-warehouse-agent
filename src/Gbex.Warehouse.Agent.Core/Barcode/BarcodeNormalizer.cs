using System.Text.RegularExpressions;

namespace Gbex.Warehouse.Agent.Core.Barcode;

public abstract record BarcodeNormalizationResult
{
    public sealed record Valid(string Barcode) : BarcodeNormalizationResult;
    public sealed record Empty : BarcodeNormalizationResult;
    public sealed record TooLong(int Length) : BarcodeNormalizationResult;
    public sealed record InvalidFormat(string Value) : BarcodeNormalizationResult;
}

/// <summary>
/// Normalizes raw USB HID keyboard-wedge scanner input. The permanent GBEX
/// barcode format itself is never used to decide manual/API behavior here:
/// Order.fulfillmentMode (surfaced via StationOrderDto) is the sole source
/// of truth for workflow logic. The agent must accept every label format the
/// GBEX web app can print:
///   - GBEX+10 digits and GBX+10 digits (legacy labels)
///   - GB+12 digits (current internal/customer-facing code)
///   - 12 bare digits (current printed/scanner code, e.g. 260924000003)
/// Scanner framing/separators around a scan are normalized, but obviously
/// wrong input (empty, absurdly long, wrong shape) is rejected before it
/// ever reaches the network.
/// </summary>
public static class BarcodeNormalizer
{
    private const int MaxReasonableLength = 64;
    private static readonly Regex GbexBarcodePattern = new(@"^((GBEX|GBX)\d{10}|GB\d{12}|\d{12}|\d{10})$", RegexOptions.Compiled);

    public static BarcodeNormalizationResult Normalize(string? rawInput)
    {
        if (rawInput is null)
        {
            return new BarcodeNormalizationResult.Empty();
        }

        var cleaned = Clean(rawInput);

        if (cleaned.Length == 0)
        {
            return new BarcodeNormalizationResult.Empty();
        }

        if (cleaned.Length > MaxReasonableLength)
        {
            return new BarcodeNormalizationResult.TooLong(cleaned.Length);
        }

        if (!GbexBarcodePattern.IsMatch(cleaned))
        {
            return new BarcodeNormalizationResult.InvalidFormat(cleaned);
        }

        return new BarcodeNormalizationResult.Valid(cleaned);
    }

    /// <summary>
    /// Returns true when two barcode strings point at the same GBEX shipment
    /// even if one side is the current internal form (GB+12 digits) and the
    /// device reported the printed/scanner form (12 bare digits).
    /// </summary>
    public static bool AreEquivalent(string? left, string? right)
    {
        var leftNormalized = Normalize(left);
        var rightNormalized = Normalize(right);

        if (leftNormalized is not BarcodeNormalizationResult.Valid leftValid
            || rightNormalized is not BarcodeNormalizationResult.Valid rightValid)
        {
            return false;
        }

        return string.Equals(
            ToComparisonKey(leftValid.Barcode),
            ToComparisonKey(rightValid.Barcode),
            StringComparison.Ordinal);
    }

    private static string Clean(string rawInput)
    {
        // Strip control characters (scanner terminators like CR/LF/Tab that
        // slipped through) and surrounding whitespace, then uppercase — the
        // format is case-insensitive at the scanner but canonical uppercase
        // everywhere downstream, matching the backend's own normalization.
        var cleaned = new string(rawInput.Where(c => !char.IsControl(c)).ToArray())
            .Trim()
            .ToUpperInvariant();

        // Some Code128 readers prepend AIM symbology identifiers such as
        // ]C0 or ]C1. They are scanner metadata, not part of the GBEX code.
        if (cleaned.Length >= 3 && cleaned[0] == ']' && cleaned[1] == 'C')
        {
            cleaned = cleaned[3..];
        }

        // Operators sometimes test with copied values containing spaces or
        // dashes. Hardware scanner output remains unchanged, but accepting
        // these harmless separators makes manual fallback safer.
        return cleaned.Replace(" ", string.Empty).Replace("-", string.Empty);
    }

    private static string ToComparisonKey(string normalizedBarcode)
    {
        if (normalizedBarcode.StartsWith("GBEX", StringComparison.Ordinal))
        {
            return normalizedBarcode[4..];
        }

        if (normalizedBarcode.StartsWith("GBX", StringComparison.Ordinal))
        {
            return normalizedBarcode[3..];
        }

        if (normalizedBarcode.StartsWith("GB", StringComparison.Ordinal))
        {
            return normalizedBarcode[2..];
        }

        return normalizedBarcode;
    }
}

/// <summary>
/// Debounces duplicate scanner submissions — a HID scanner double-firing
/// (mechanical bounce, or an operator holding the trigger) must not be
/// treated as two separate scan events for the same barcode.
/// </summary>
public sealed class ScanDebouncer
{
    private readonly TimeSpan _window;
    private readonly Core.Abstractions.IClock _clock;
    private string? _lastBarcode;
    private DateTimeOffset _lastAt;

    public ScanDebouncer(Core.Abstractions.IClock clock, TimeSpan? window = null)
    {
        _clock = clock;
        _window = window ?? TimeSpan.FromMilliseconds(750);
    }

    /// <summary>Returns true if this scan should be processed (not a debounced duplicate).</summary>
    public bool ShouldProcess(string barcode)
    {
        var now = _clock.UtcNow;
        if (_lastBarcode == barcode && now - _lastAt < _window)
        {
            return false;
        }

        _lastBarcode = barcode;
        _lastAt = now;
        return true;
    }
}
