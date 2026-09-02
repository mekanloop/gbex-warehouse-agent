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
/// barcode format itself — GBEX+10 digits (manual fulfillment, and every
/// historical order from before the split existed, including old
/// live-carrier ones) OR GBX+10 digits (new live-carrier/API orders) — is
/// never altered here, only whitespace/control characters around a scan are
/// trimmed. See gbex website repo's lib/barcode.ts isValidGbexBarcode: the
/// prefix is a human-readability hint only, never something code branches
/// on — Order.fulfillmentMode (surfaced via StationOrderDto) is the sole
/// source of truth for manual-vs-API workflow logic. Obviously-wrong input
/// (empty, absurdly long, wrong shape) is rejected before it ever reaches
/// the network.
/// </summary>
public static class BarcodeNormalizer
{
    private const int MaxReasonableLength = 64;
    private static readonly Regex GbexBarcodePattern = new(@"^(GBEX|GBX)\d{10}$", RegexOptions.Compiled);

    public static BarcodeNormalizationResult Normalize(string? rawInput)
    {
        if (rawInput is null)
        {
            return new BarcodeNormalizationResult.Empty();
        }

        // Strip control characters (scanner terminators like CR/LF/Tab that
        // slipped through) and surrounding whitespace, then uppercase — the
        // format is case-insensitive at the scanner but canonical uppercase
        // everywhere downstream, matching the backend's own normalization.
        var cleaned = new string(rawInput.Where(c => !char.IsControl(c)).ToArray()).Trim().ToUpperInvariant();

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
