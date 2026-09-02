namespace Gbex.Warehouse.Agent.Core.Update;

/// <summary>Wire shape of GET /api/warehouse/agent-version's "available" response — see docs/GBEX_API_CONTRACT.md.</summary>
public sealed record AgentUpdateManifest
{
    public required string LatestVersion { get; init; }
    /// <summary>Always a path on the SAME GBEX host, authenticated with the same station Bearer token — never a direct third-party/bucket URL.</summary>
    public required string InstallerUrl { get; init; }
    public required string Sha256 { get; init; }
    public string? ReleaseNotes { get; init; }
    public required bool Mandatory { get; init; }
}
