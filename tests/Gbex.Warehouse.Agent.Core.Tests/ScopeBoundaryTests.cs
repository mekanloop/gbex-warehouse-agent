using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

/// <summary>
/// Structural guard: the Agent must NEVER contain wallet, pricing,
/// carrier-selection, customer-approval, or shipment-replacement logic, and
/// must never reference Karrio or database credentials. Same
/// source-inspection pattern the gbex website repo itself uses for its own
/// leak guards — reading real source text, not reflection, so it catches
/// even code that would compile fine but shouldn't exist here at all.
/// </summary>
public class ScopeBoundaryTests
{
    private static readonly string[] SourceDirs = { "src", "simulator" };

    private static readonly (string Term, string Reason)[] ForbiddenTerms =
    {
        ("Karrio", "the Agent must never reference the carrier API"),
        ("karrio", "the Agent must never reference the carrier API"),
        ("wallet", "the Agent must never contain wallet logic"),
        ("Wallet", "the Agent must never contain wallet logic"),
        ("chargedAmount", "the Agent must never see or compute a price"),
        ("carrierLabelUrl", "the Agent must never see a carrier label"),
        ("carrierTrackingNumber", "the Agent must never see a carrier tracking number"),
        ("carrierName", "the Agent must never see carrier identity"),
        ("SETTINGS_ENCRYPTION_KEY", "the Agent must never reference the GBEX backend's own secret material"),
        ("DATABASE_URL", "the Agent must never hold a database credential"),
        ("holdFunds", "the Agent must never call wallet primitives"),
        ("debitForCorrection", "the Agent must never call wallet primitives"),
        ("creditWallet", "the Agent must never call wallet primitives"),
    };

    public static IEnumerable<object[]> AllSourceFiles() =>
        // StationOrderDto.cs is intentionally excluded: it legitimately spells
        // out these exact terms as a DENYLIST (ForbiddenStationFields) — its
        // own dedicated test below checks they appear ONLY there, never in
        // the actual DTO shape.
        RepoScan.AllSourceFiles(SourceDirs)
            .Where(f => !f.EndsWith("StationOrderDto.cs", StringComparison.Ordinal))
            .Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(AllSourceFiles))]
    public void No_source_file_references_forbidden_financial_or_carrier_terms(string file)
    {
        var text = File.ReadAllText(file);
        foreach (var (term, reason) in ForbiddenTerms)
        {
            Assert.False(text.Contains(term, StringComparison.Ordinal), $"{file} contains forbidden term '{term}' — {reason}");
        }
    }

    [Fact]
    public void StationOrderDto_carries_no_customer_PII_or_carrier_or_price_fields()
    {
        var file = Path.Combine(RepoScan.RepoRoot, "src", "Gbex.Warehouse.Agent.Core", "Models", "StationOrderDto.cs");
        var text = File.ReadAllText(file);

        // The DTO record itself (before ForbiddenStationFields' own list,
        // which legitimately NAMES these terms as strings to check against).
        var dtoBlock = text[..text.IndexOf("ForbiddenStationFields", StringComparison.Ordinal)];

        foreach (var forbidden in new[] { "SenderInfo", "RecipientInfo", "ChargedAmount", "Currency", "CarrierName", "CarrierLabelUrl", "CarrierTrackingNumber", "KarrioShipmentId", "Balance" })
        {
            Assert.DoesNotContain(forbidden, dtoBlock, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Core_project_has_zero_dependency_on_WPF_or_Windows_specific_assemblies()
    {
        var csprojPath = Path.Combine(RepoScan.RepoRoot, "src", "Gbex.Warehouse.Agent.Core", "Gbex.Warehouse.Agent.Core.csproj");
        var text = StripXmlComments(File.ReadAllText(csprojPath));

        Assert.DoesNotContain("UseWPF", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-windows", text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Security.Cryptography.ProtectedData", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Infrastructure_project_has_zero_dependency_on_WPF_or_DPAPI()
    {
        var csprojPath = Path.Combine(RepoScan.RepoRoot, "src", "Gbex.Warehouse.Agent.Infrastructure", "Gbex.Warehouse.Agent.Infrastructure.csproj");
        var text = StripXmlComments(File.ReadAllText(csprojPath));

        Assert.DoesNotContain("UseWPF", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-windows", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectedData", text, StringComparison.Ordinal);
    }

    /// <summary>Strips &lt;!-- --&gt; XML comments so a doc comment merely EXPLAINING what this project deliberately avoids (e.g. "NOT UseWPF") doesn't itself trip the check.</summary>
    private static string StripXmlComments(string xml) =>
        System.Text.RegularExpressions.Regex.Replace(xml, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);

    [Fact]
    public void ILabelPrinter_never_references_a_real_carrier_or_label_rendering_library()
    {
        var file = Path.Combine(RepoScan.RepoRoot, "src", "Gbex.Warehouse.Agent.Core", "Abstractions", "ILabelPrinter.cs");
        var text = File.ReadAllText(file);
        Assert.DoesNotContain("Karrio", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PDF", text, StringComparison.Ordinal);
    }
}
