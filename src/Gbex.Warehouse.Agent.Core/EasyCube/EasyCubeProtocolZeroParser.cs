using Gbex.Warehouse.Agent.Core.Units;

namespace Gbex.Warehouse.Agent.Core.EasyCube;

/// <summary>
/// One EasyCube TCP/IP Protocol measurement record, still in the device's
/// own raw units (see EasyCubeTcpListener for the KG/CM-normalized
/// CapturedMeasurement this becomes). Field names mirror the wire protocol's
/// own short tags (DSN, P, T, L, W, H, WT, ...), not the Web API's JSON
/// field names — the two are separate manufacturer-documented surfaces of
/// the same device (see docs/EASYCUBE_CONTRACT.md).
/// </summary>
public sealed record EasyCubeProtocolRecord
{
    public required string DeviceSerial { get; init; }
    public required string PackageNumber { get; init; }
    public required string TimestampRaw { get; init; }
    public required decimal Length { get; init; }
    public string? LengthUnit { get; init; }
    public required decimal Width { get; init; }
    public string? WidthUnit { get; init; }
    public required decimal Height { get; init; }
    public string? HeightUnit { get; init; }
    public required decimal Weight { get; init; }
    public string? WeightUnit { get; init; }
    public decimal? DimensionalWeight { get; init; }
    public string? DimensionalWeightUnit { get; init; }
    /// <summary>The barcode EasyCube itself read from the scanner wired into its own USB port — the PRIMARY correlation key for the push-driven flow (there is no separate "operator scanned this on the PC" value to cross-check against in this flow, unlike the HTTP/keyboard-wedge fallback).</summary>
    public string? Barcode { get; init; }
}

public abstract record EasyCubeFrameParseResult
{
    public sealed record Ok(EasyCubeProtocolRecord Record) : EasyCubeFrameParseResult;
    public sealed record Malformed(string Detail) : EasyCubeFrameParseResult;
}

/// <summary>
/// Parses the manufacturer's real "EasyCube TCP/IP Protocol" wire format,
/// transcribed from "EasyCube Static Dimensioner Software Guide_V01_EN.pdf"
/// section "EasyCube TCP/IP Protocol" (pages 3-16) — a DIFFERENT surface
/// from the Web API's JSON responses that EasyCubeClient/EasyCubeDtos.cs
/// already cover. Selected on the device via /tcps_config's "Protocol"
/// setting (0 = native EasyCube wire format; 1/2 emulate Cubiscan/QubeVu
/// instead for other WMS integrations) — this Agent only ever speaks
/// Protocol 0, matching requirement to parse "gerçek üretici biçiminde".
///
/// Wire shape (from the guide's "MFR" — get full measurement record —
/// command's documented response):
///   {MFR,DSN,&lt;serial&gt;,P,&lt;package-number&gt;,T,&lt;yyyy-MM-dd HH:mm:ss&gt;,
///    L,&lt;length&gt;,LU,&lt;unit&gt;,W,&lt;width&gt;,WU,&lt;unit&gt;,H,&lt;height&gt;,HU,&lt;unit&gt;,
///    WT,&lt;weight&gt;,WTU,&lt;unit&gt;,DWT,&lt;dim-weight&gt;,DWTU,&lt;unit&gt;,...,B,&lt;barcode&gt;}
/// i.e. curly-brace-delimited, comma-separated, with the leading token
/// naming the record type followed by a FLAT alternating key,value,key,value
/// list (not "key=value" pairs) for the rest.
///
/// FLAGGED ASSUMPTION — VERIFY ON PHYSICAL HARDWARE: the guide's own worked
/// example for the "MFR" command shows the response leading with "{M,DSN,..."
/// rather than "{MFR,DSN,..." as its own "Response" template row specifies —
/// an inconsistency in the manufacturer's document, not in this code. This
/// parser accepts both "MFR" and "M" (and "MAR", the separately-documented
/// archived/alibi-record tag, which carries the identical field set plus an
/// extra VAL flag this Agent does not need) as valid leading tokens for a
/// measurement record. The guide documents a Data-Auto-Send ("DAS") flag on
/// the device's TCP/IP settings but never states in narrative text what an
/// unsolicited auto-push frame looks like — this parser assumes it is
/// byte-for-byte the same shape as the documented "MFR" pull response
/// (the only measurement-record shape the guide defines at all). Confirm
/// both assumptions against a real device before relying on them in the
/// field.
/// </summary>
public static class EasyCubeProtocolZeroParser
{
    private static readonly HashSet<string> RecordTags = new(StringComparer.OrdinalIgnoreCase) { "MFR", "M", "MAR" };

    /// <summary>
    /// Extracts every complete "{...}" frame currently sitting in `buffer`.
    /// TCP gives no message boundaries of its own, so a single read can
    /// contain a partial frame (fragmentation), exactly one complete frame,
    /// or several concatenated frames (the device queuing up more than one
    /// measurement before the Agent's next read) — this handles all three
    /// the same way: extract every complete frame found, and always return
    /// any trailing incomplete frame as `Remainder` for the caller to
    /// prepend to the next read rather than losing it.
    /// </summary>
    public static (IReadOnlyList<string> Frames, string Remainder) ExtractFrames(string buffer)
    {
        var frames = new List<string>();
        var searchStart = 0;

        while (true)
        {
            var open = buffer.IndexOf('{', searchStart);
            if (open < 0)
            {
                // No frame start left at all — any bytes before here were
                // stray noise (e.g. a corrupted partial tail from a dropped
                // connection) and are discarded rather than buffered forever.
                return (frames, "");
            }

            var close = buffer.IndexOf('}', open + 1);
            if (close < 0)
            {
                return (frames, buffer[open..]);
            }

            frames.Add(buffer[(open + 1)..close]);
            searchStart = close + 1;
        }
    }

    public static EasyCubeFrameParseResult TryParse(string frameContent)
    {
        var tokens = frameContent.Split(',');
        if (tokens.Length == 0 || !RecordTags.Contains(tokens[0].Trim()))
        {
            return new EasyCubeFrameParseResult.Malformed($"unrecognized frame tag '{(tokens.Length > 0 ? tokens[0].Trim() : "")}'");
        }

        if ((tokens.Length - 1) % 2 != 0)
        {
            return new EasyCubeFrameParseResult.Malformed("odd number of key/value tokens in frame");
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < tokens.Length; i += 2)
        {
            fields[tokens[i].Trim()] = tokens[i + 1].Trim();
        }

        if (!fields.TryGetValue("DSN", out var dsn) || string.IsNullOrWhiteSpace(dsn))
        {
            return new EasyCubeFrameParseResult.Malformed("missing DSN (device serial)");
        }
        if (!fields.TryGetValue("P", out var packageNumber) || string.IsNullOrWhiteSpace(packageNumber))
        {
            return new EasyCubeFrameParseResult.Malformed("missing P (package number)");
        }
        if (!fields.TryGetValue("T", out var timestamp) || string.IsNullOrWhiteSpace(timestamp))
        {
            return new EasyCubeFrameParseResult.Malformed("missing T (timestamp)");
        }

        if (!TryParseRequiredDecimal(fields, "L", out var length, out var lengthError)) return new EasyCubeFrameParseResult.Malformed(lengthError!);
        if (!TryParseRequiredDecimal(fields, "W", out var width, out var widthError)) return new EasyCubeFrameParseResult.Malformed(widthError!);
        if (!TryParseRequiredDecimal(fields, "H", out var height, out var heightError)) return new EasyCubeFrameParseResult.Malformed(heightError!);
        if (!TryParseRequiredDecimal(fields, "WT", out var weight, out var weightError)) return new EasyCubeFrameParseResult.Malformed(weightError!);

        decimal? dimWeight = null;
        if (fields.TryGetValue("DWT", out var dwtRaw) && UnitConverter.ParseInvariantDecimal(dwtRaw) is UnitParseResult.Ok dwtOk)
        {
            dimWeight = dwtOk.Value;
        }

        fields.TryGetValue("LU", out var lengthUnit);
        fields.TryGetValue("WU", out var widthUnit);
        fields.TryGetValue("HU", out var heightUnit);
        fields.TryGetValue("WTU", out var weightUnit);
        fields.TryGetValue("DWTU", out var dwtUnit);
        fields.TryGetValue("B", out var barcode);

        return new EasyCubeFrameParseResult.Ok(new EasyCubeProtocolRecord
        {
            DeviceSerial = dsn,
            PackageNumber = packageNumber,
            TimestampRaw = timestamp,
            Length = length,
            LengthUnit = lengthUnit,
            Width = width,
            WidthUnit = widthUnit,
            Height = height,
            HeightUnit = heightUnit,
            Weight = weight,
            WeightUnit = weightUnit,
            DimensionalWeight = dimWeight,
            DimensionalWeightUnit = dwtUnit,
            Barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode,
        });
    }

    private static bool TryParseRequiredDecimal(Dictionary<string, string> fields, string key, out decimal value, out string? error)
    {
        value = 0m;
        if (!fields.TryGetValue(key, out var raw))
        {
            error = $"missing {key}";
            return false;
        }
        if (UnitConverter.ParseInvariantDecimal(raw) is not UnitParseResult.Ok ok)
        {
            error = $"unreadable {key} value '{raw}'";
            return false;
        }
        value = ok.Value;
        error = null;
        return true;
    }
}
