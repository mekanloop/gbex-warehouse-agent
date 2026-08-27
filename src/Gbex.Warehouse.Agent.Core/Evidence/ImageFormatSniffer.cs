namespace Gbex.Warehouse.Agent.Core.Evidence;

/// <summary>
/// Detects an image's real format from its magic bytes. Needed because
/// EasyCube's own documentation and worked examples never state what image
/// format ImgBase64 actually contains, and this Agent previously hardcoded
/// "image/jpeg" everywhere — a real device (2026-08-27) was confirmed to
/// send PNG data instead, which TemporaryImageStore's own format-vs-declared-
/// type check then rejected outright, silently losing every mismatch's
/// evidence photo. Callers must sniff the real format from the decoded bytes
/// themselves rather than assuming a format, both when saving locally and
/// when uploading to the GBEX backend (which performs the same magic-byte
/// validation server-side).
/// </summary>
public static class ImageFormatSniffer
{
    public static string? Sniff(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A) return "image/png";
        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P') return "image/webp";
        return null;
    }
}
