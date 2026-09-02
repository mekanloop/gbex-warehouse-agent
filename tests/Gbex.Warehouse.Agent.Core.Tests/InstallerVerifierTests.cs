using System.Security.Cryptography;
using System.Text;
using Gbex.Warehouse.Agent.Core.Update;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

public class InstallerVerifierTests
{
    [Fact]
    public void VerifySha256_accepts_bytes_matching_the_expected_hash()
    {
        var bytes = Encoding.UTF8.GetBytes("fake installer contents");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.True(InstallerVerifier.VerifySha256(bytes, hash));
    }

    [Fact]
    public void VerifySha256_is_case_insensitive_on_the_expected_hash()
    {
        var bytes = Encoding.UTF8.GetBytes("fake installer contents");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToUpperInvariant();

        Assert.True(InstallerVerifier.VerifySha256(bytes, hash));
    }

    [Fact]
    public void VerifySha256_rejects_a_tampered_or_corrupted_download()
    {
        var bytes = Encoding.UTF8.GetBytes("fake installer contents");
        var wrongHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("different contents"))).ToLowerInvariant();

        Assert.False(InstallerVerifier.VerifySha256(bytes, wrongHash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void VerifySha256_rejects_a_missing_or_blank_expected_hash(string? expected)
    {
        var bytes = Encoding.UTF8.GetBytes("fake installer contents");
        Assert.False(InstallerVerifier.VerifySha256(bytes, expected!));
    }

    [Theory]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.9.0", "1.9.0", false)]
    [InlineData("1.8.0", "1.9.0", false)]
    public void IsNewerVersion_compares_well_formed_versions_correctly(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, InstallerVerifier.IsNewerVersion(candidate, current));
    }

    [Theory]
    [InlineData("not-a-version", "1.9.0")]
    [InlineData("1.10.0", "not-a-version")]
    [InlineData("", "1.9.0")]
    public void IsNewerVersion_is_conservative_and_refuses_to_compare_malformed_input(string candidate, string current)
    {
        // A malformed manifest value must never be treated as "obviously
        // newer" — that could turn a misconfigured server response into a
        // forced update loop.
        Assert.False(InstallerVerifier.IsNewerVersion(candidate, current));
    }
}
