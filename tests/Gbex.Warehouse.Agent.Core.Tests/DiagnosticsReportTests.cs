using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Infrastructure.Diagnostics;
using Gbex.Warehouse.Agent.Infrastructure.Secrets;
using Moq;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

public class DiagnosticsReportTests
{
    [Fact]
    public void DiagnosticsReport_has_no_field_that_could_hold_a_secret_or_customer_fact()
    {
        var properties = typeof(DiagnosticsReport).GetProperties();

        // "StationSecretConfigured" is deliberate and safe: a boolean
        // PRESENCE flag, never the secret value. Any property whose name
        // mentions Secret/Token/Password must be exactly this kind of
        // bool flag, never a string that could hold the real value.
        foreach (var prop in properties.Where(p => p.Name.Contains("Secret") || p.Name.Contains("Token") || p.Name.Contains("Password")))
        {
            Assert.Equal(typeof(bool), prop.PropertyType);
        }

        var propNames = properties.Select(p => p.Name).ToList();
        foreach (var forbidden in new[] { "ChargedAmount", "Price", "SenderInfo", "RecipientInfo", "CarrierName", "TrackingNumber", "KarrioShipmentId" })
        {
            Assert.DoesNotContain(propNames, p => p.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task BuildAsync_reports_station_secret_ONLY_as_a_boolean_presence_flag()
    {
        const string realSecret = "wst_do_not_leak_me_1234567890";
        var secretStore = new InMemorySecretStore();
        await secretStore.SaveStationSecretAsync(realSecret, CancellationToken.None);

        var outbox = new Mock<IOutboxStore>();
        outbox.Setup(o => o.CountPendingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        outbox.Setup(o => o.CountByStateAsync(It.IsAny<OutboxItemState>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        outbox.Setup(o => o.GetRecentSanitizedErrorsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());

        var report = await DiagnosticsReportBuilder.BuildAsync(
            "1.0.0", "https://app.gbex.com.tr", "Connected", DateTimeOffset.UtcNow,
            secretStore, "http://localhost:8080", "Connected", "EasyCube-1.6", "3.0", "dev-1",
            outbox.Object, CancellationToken.None);

        Assert.True(report.StationSecretConfigured);

        var rendered = DiagnosticsReportBuilder.RenderAsText(report);
        Assert.DoesNotContain(realSecret, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("wst_", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsText_never_contains_a_recent_error_string_that_looks_like_an_authorization_header()
    {
        var secretStore = new InMemorySecretStore();
        var outbox = new Mock<IOutboxStore>();
        outbox.Setup(o => o.CountPendingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
        outbox.Setup(o => o.CountByStateAsync(It.IsAny<OutboxItemState>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        outbox.Setup(o => o.GetRecentSanitizedErrorsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "http_502", "timeout", "network" });

        var report = await DiagnosticsReportBuilder.BuildAsync(
            "1.0.0", "https://app.gbex.com.tr", "Degraded", null,
            secretStore, "http://localhost:8080", "Offline", null, null, null,
            outbox.Object, CancellationToken.None);

        var rendered = DiagnosticsReportBuilder.RenderAsText(report);

        Assert.Contains("http_502", rendered);
        Assert.Contains("2", rendered); // offline queue count
        Assert.DoesNotContain("Bearer", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoScan_finds_no_forbidden_terms_in_the_Diagnostics_folder()
    {
        var diagnosticsDir = Path.Combine(RepoScan.RepoRoot, "src", "Gbex.Warehouse.Agent.Infrastructure", "Diagnostics");
        foreach (var file in Directory.EnumerateFiles(diagnosticsDir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[] { "Karrio", "wallet", "chargedAmount", "holdFunds" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
