namespace Gbex.Warehouse.Agent.Core.EasyCube;

/// <summary>
/// Defensive Base64 decoding for EasyCube's ImgBase64 field. Real hardware
/// has already been found deviating from the manufacturer's documented
/// field NAMES ("PackageLength" vs "PackageLenght") and TYPES
/// (DimWeightFactorType as a string, not an int) — see
/// EasyCubeMeasurementResponse's flagged findings. The base64 payload
/// itself is not guaranteed to be in .NET's strict canonical format either:
/// a "data:image/...;base64," URI prefix, literal quote characters, the
/// base64url alphabet, missing padding, or embedded whitespace are all
/// things embedded-device firmware has been observed to emit. This tries a
/// series of increasingly permissive interpretations rather than giving up
/// after the first FormatException — a real evidence photo must never be
/// silently lost over a formatting quirk that a human eye would consider
/// obviously fixable.
/// </summary>
public static class EasyCubeImageDecoder
{
    public static byte[]? TryDecode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var candidate = raw.Trim();

        // "data:image/jpeg;base64,/9j/4AAQ..." -> keep only what follows the comma.
        var commaIndex = candidate.IndexOf(',');
        if (candidate.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
        {
            candidate = candidate[(commaIndex + 1)..];
        }

        // Some firmware embeds literal quote characters inside the string value itself.
        candidate = candidate.Trim('"', '\'');

        // Strip whitespace/newlines some devices chunk their output with.
        candidate = RemoveWhitespace(candidate);

        if (TryStandardDecode(candidate, out var bytes)) return bytes;

        // base64url alphabet (-, _ instead of +, /), commonly emitted with no padding.
        var urlSafe = candidate.Replace('-', '+').Replace('_', '/');
        if (TryStandardDecode(urlSafe, out bytes)) return bytes;

        // Try restoring padding a device may have omitted, for both alphabets.
        if (TryStandardDecode(WithPadding(candidate), out bytes)) return bytes;
        if (TryStandardDecode(WithPadding(urlSafe), out bytes)) return bytes;

        return null;
    }

    private static string RemoveWhitespace(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var written = 0;
        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c)) buffer[written++] = c;
        }
        return new string(buffer[..written]);
    }

    private static string WithPadding(string value)
    {
        var remainder = value.Length % 4;
        return remainder == 0 ? value : value + new string('=', 4 - remainder);
    }

    private static bool TryStandardDecode(string value, out byte[]? bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = null;
            return false;
        }
    }
}
