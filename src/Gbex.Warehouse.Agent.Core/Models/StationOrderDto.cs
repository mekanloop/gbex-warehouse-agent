namespace Gbex.Warehouse.Agent.Core.Models;

/// <summary>
/// Mirrors GBEX's StationOrderDTO exactly (lib/dto/warehouse-order.ts in the
/// gbex website repo) — the ONLY shape a station may ever receive. No
/// carrier identity, no price/currency, no customer PII. Do not add fields
/// here that are not present in the real backend response; the Agent must
/// never invent or assume a richer contract than what the station endpoint
/// actually returns.
/// </summary>
public sealed record StationOrderDto
{
    public required string Id { get; init; }
    public required string GbexBarcode { get; init; }
    public required string Status { get; init; }
    public required string DestinationCountry { get; init; }
    public required string DestinationCity { get; init; }
    public required decimal DeclaredWeight { get; init; }
    public required decimal DeclaredDesi { get; init; }
    public required decimal DeclaredLength { get; init; }
    public required decimal DeclaredWidth { get; init; }
    public required decimal DeclaredHeight { get; init; }
    /// <summary>
    /// Order.fulfillmentMode, verbatim ("manual_carrier" or "live_carrier") —
    /// the ONLY thing the Agent should ever branch manual-vs-API workflow
    /// logic on. The GBEX/GBX barcode prefix is a human-readability hint
    /// only (a handful of historical live-Karrio orders still carry the old
    /// GBEX prefix) — never decide behavior from it.
    /// </summary>
    public required string FulfillmentMode { get; init; }
    /// <summary>
    /// True whenever the operator still needs to run the manual label
    /// workflow instead of relying on an already-purchased Karrio label. A
    /// manual order can and must still be measurable before a real carrier
    /// label is matched — this flag is informational for the operator's
    /// screen, never a gate on the measurement flow itself.
    /// </summary>
    public required bool RequiresManualCarrierLabel { get; init; }
    /// <summary>Present only for manual_carrier orders — null for live_carrier ones, matching the backend omitting the field entirely rather than sending an empty string.</summary>
    public string? ManualFulfillmentStatus { get; init; }
}

/// <summary>Field names that must never appear anywhere in an Agent DTO or log — mirrors FORBIDDEN_STATION_FIELDS on the backend.</summary>
public static class ForbiddenStationFields
{
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "carrierName",
        "carrierLabelUrl",
        "carrierTrackingNumber",
        "karrioShipmentId",
        "carrierShipments",
        "chargedAmount",
        "currency",
        "senderInfo",
        "recipientInfo",
        "apiKeyHash",
        "balance",
    };
}
