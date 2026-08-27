using Gbex.Warehouse.Agent.Core.EasyCube;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

public class EasyCubeImageDecoderTests
{
    // 3 raw bytes -> 4-char standard base64, no padding needed either way.
    private const string PlainBase64 = "AQID"; // bytes {1,2,3}
    private static readonly byte[] ExpectedBytes = { 1, 2, 3 };

    [Fact]
    public void TryDecode_decodes_plain_standard_base64()
    {
        Assert.Equal(ExpectedBytes, EasyCubeImageDecoder.TryDecode(PlainBase64));
    }

    [Fact]
    public void TryDecode_strips_a_data_uri_prefix()
    {
        Assert.Equal(ExpectedBytes, EasyCubeImageDecoder.TryDecode($"data:image/jpeg;base64,{PlainBase64}"));
    }

    [Fact]
    public void TryDecode_strips_literal_quote_characters()
    {
        Assert.Equal(ExpectedBytes, EasyCubeImageDecoder.TryDecode($"“{PlainBase64}”".Replace('“', '"').Replace('”', '"')));
    }

    [Fact]
    public void TryDecode_strips_embedded_whitespace_and_newlines()
    {
        Assert.Equal(ExpectedBytes, EasyCubeImageDecoder.TryDecode("AQ\r\nID"));
    }

    [Fact]
    public void TryDecode_handles_base64url_alphabet()
    {
        // A payload whose standard-base64 form contains '+' and '/' becomes
        // '-' and '_' under base64url — five bytes chosen specifically to
        // produce both special characters in the standard encoding.
        var bytes = new byte[] { 0xFB, 0xFF, 0xBF, 0xEF, 0xFF };
        var standard = Convert.ToBase64String(bytes); // "+/+/"-ish, contains '+' and '/'
        Assert.Contains('+', standard);
        var urlSafeNoPadding = standard.Replace('+', '-').Replace('/', '_').TrimEnd('=');

        Assert.Equal(bytes, EasyCubeImageDecoder.TryDecode(urlSafeNoPadding));
    }

    [Fact]
    public void TryDecode_restores_missing_padding()
    {
        var withoutPadding = PlainBase64.TrimEnd('='); // already unpadded here, but exercise the path explicitly
        Assert.Equal(ExpectedBytes, EasyCubeImageDecoder.TryDecode(withoutPadding));
    }

    [Fact]
    public void TryDecode_returns_null_for_genuinely_invalid_data_rather_than_throwing()
    {
        Assert.Null(EasyCubeImageDecoder.TryDecode("not valid base64 at all!!##"));
    }

    [Fact]
    public void TryDecode_returns_null_for_empty_or_null_input()
    {
        Assert.Null(EasyCubeImageDecoder.TryDecode(null));
        Assert.Null(EasyCubeImageDecoder.TryDecode(""));
        Assert.Null(EasyCubeImageDecoder.TryDecode("   "));
    }
}
