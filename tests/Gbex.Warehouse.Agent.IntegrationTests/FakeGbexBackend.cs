using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Gbex.Warehouse.Agent.IntegrationTests;

/// <summary>
/// A minimal, real Kestrel-backed fake of GBEX's machine-authenticated
/// warehouse routes, mirroring their exact contract (see
/// docs/GBEX_API_CONTRACT.md) closely enough to exercise the real
/// GbexApiClient end-to-end, INCLUDING its own idempotency behavior — the
/// same Idempotency-Key submitted twice returns the SAME stored result
/// rather than creating a second measurement, exactly like the real
/// backend's claimIdempotencyKey. Test-only; never shipped.
/// </summary>
public sealed class FakeGbexBackend : IAsyncDisposable
{
    public string ValidToken { get; } = "wst_" + Guid.NewGuid().ToString("N");
    public string StationName { get; } = "TEST-STATION-SIM";
    public bool StationDisabled { get; set; }
    public bool StationRevoked { get; set; }

    public string OrderId { get; } = "order_sim_1";
    public string ExpectedBarcode { get; set; } = "GBEX2508230001";
    public decimal DeclaredWeight { get; set; } = 5;
    public decimal DeclaredLength { get; set; } = 40;
    public decimal DeclaredWidth { get; set; } = 30;
    public decimal DeclaredHeight { get; set; } = 20;
    public string FulfillmentMode { get; set; } = "live_carrier";
    public bool RequiresManualCarrierLabel { get; set; }
    public string? ManualFulfillmentStatus { get; set; }

    /// <summary>Configures whether the NEXT fresh (non-replayed) submission returns pass or mismatch.</summary>
    public string NextResult { get; set; } = "mismatch";

    private readonly ConcurrentDictionary<string, (int Status, object Body)> _idempotencyResults = new();
    public int MeasurementSubmitCallCount;
    public int EvidenceUploadCallCount;
    public readonly List<string> UploadedEvidenceMeasurementIds = new();

    private WebApplication? _app;
    public string BaseUrl { get; private set; } = "";

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        _app.MapPost("/api/warehouse/heartbeat", (HttpContext ctx) =>
        {
            if (!TryAuthenticate(ctx, out var unauthorized)) return unauthorized;
            return Results.Ok(new { ok = true, station = StationName });
        });

        _app.MapPost("/api/warehouse/orders/lookup", async (HttpContext ctx) =>
        {
            if (!TryAuthenticate(ctx, out var unauthorized)) return unauthorized;
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object>>() ?? new();
            var barcode = body.TryGetValue("barcode", out var b) ? b?.ToString() : null;
            if (barcode != ExpectedBarcode)
            {
                return Results.Json(new { message = "not found" }, statusCode: 404);
            }
            return Results.Ok(new
            {
                order = new
                {
                    id = OrderId,
                    gbexBarcode = ExpectedBarcode,
                    status = "on_hold",
                    destinationCountry = "DE",
                    destinationCity = "Berlin",
                    declaredWeight = DeclaredWeight,
                    declaredDesi = 5,
                    declaredLength = DeclaredLength,
                    declaredWidth = DeclaredWidth,
                    declaredHeight = DeclaredHeight,
                    fulfillmentMode = FulfillmentMode,
                    requiresManualCarrierLabel = RequiresManualCarrierLabel,
                    manualFulfillmentStatus = ManualFulfillmentStatus,
                },
            });
        });

        _app.MapPost("/api/warehouse/measurements", async (HttpContext ctx) =>
        {
            if (!TryAuthenticate(ctx, out var unauthorized)) return unauthorized;
            var idempotencyKey = ctx.Request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrEmpty(idempotencyKey))
            {
                return Results.Json(new { message = "Idempotency-Key required" }, statusCode: 422);
            }

            if (_idempotencyResults.TryGetValue(idempotencyKey, out var replay))
            {
                // SAME key seen before — return the exact same stored result,
                // do NOT create a second measurement. This is the real
                // backend's replay behavior, and is exactly what the
                // required E2E scenario's "retry same operation -> confirm
                // only one measurement result" step verifies.
                return Results.Json(replay.Body, statusCode: replay.Status);
            }

            Interlocked.Increment(ref MeasurementSubmitCallCount);
            var measurementId = "meas_" + Guid.NewGuid().ToString("N")[..12];
            var responseBody = new { measurementId, result = NextResult, requiresEvidence = NextResult == "mismatch" };
            _idempotencyResults[idempotencyKey] = (201, responseBody);
            return Results.Json(responseBody, statusCode: 201);
        });

        _app.MapPost("/api/warehouse/measurements/{id}/evidence", async (HttpContext ctx, string id) =>
        {
            if (!TryAuthenticate(ctx, out var unauthorized)) return unauthorized;
            var idempotencyKey = ctx.Request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrEmpty(idempotencyKey))
            {
                return Results.Json(new { message = "Idempotency-Key required" }, statusCode: 422);
            }

            if (_idempotencyResults.TryGetValue(idempotencyKey, out var replay))
            {
                return Results.Json(replay.Body, statusCode: replay.Status);
            }

            if (!ctx.Request.HasFormContentType)
            {
                return Results.Json(new { message = "photo alanı zorunludur." }, statusCode: 422);
            }
            var form = await ctx.Request.ReadFormAsync();
            var photo = form.Files["photo"];
            if (photo is null || photo.Length == 0)
            {
                return Results.Json(new { message = "photo alanı zorunludur." }, statusCode: 422);
            }

            Interlocked.Increment(ref EvidenceUploadCallCount);
            lock (UploadedEvidenceMeasurementIds) UploadedEvidenceMeasurementIds.Add(id);
            var responseBody = new { ok = true, photoUrl = $"https://fake-storage.invalid/{id}.jpg" };
            _idempotencyResults[idempotencyKey] = (200, responseBody);
            return Results.Json(responseBody, statusCode: 200);
        });

        _app.MapGet("/api/warehouse/agent-version", (HttpContext ctx) =>
        {
            if (!TryAuthenticate(ctx, out var unauthorized)) return unauthorized;
            if (AgentReleaseVersion is null) return Results.Ok(new { available = false });
            return Results.Ok(new
            {
                available = true,
                latestVersion = AgentReleaseVersion,
                installerUrl = "/api/warehouse/agent-version/download",
                sha256 = AgentReleaseSha256,
                releaseNotes = AgentReleaseNotes,
                mandatory = AgentReleaseMandatory,
            });
        });

        _app.MapGet("/api/warehouse/agent-version/download", (HttpContext ctx) =>
        {
            if (!TryAuthenticate(ctx, out var unauthorized)) return unauthorized;
            if (AgentReleaseBytes is null) return Results.Json(new { message = "no release" }, statusCode: 404);
            return Results.Bytes(AgentReleaseBytes, "application/vnd.microsoft.portable-executable");
        });

        await _app.StartAsync();
        BaseUrl = _app.Urls.First();
    }

    /// <summary>Null = "no release published" (available:false), matching the real backend's healthy-empty-state response.</summary>
    public string? AgentReleaseVersion { get; set; }
    public string? AgentReleaseSha256 { get; set; }
    public string? AgentReleaseNotes { get; set; }
    public bool AgentReleaseMandatory { get; set; }
    public byte[]? AgentReleaseBytes { get; set; }

    private bool TryAuthenticate(HttpContext ctx, out IResult unauthorizedResult)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ") ? header["Bearer ".Length..] : null;

        if (StationRevoked || token != ValidToken)
        {
            unauthorizedResult = Results.Json(new { message = "Yetkisiz erişim." }, statusCode: 401);
            return false;
        }
        if (StationDisabled)
        {
            // Matches the real backend's known gap (see docs/BACKEND_CHANGES_NEEDED.md):
            // a disabled station ALSO gets a 401, not a distinct status.
            unauthorizedResult = Results.Json(new { message = "Yetkisiz erişim." }, statusCode: 401);
            return false;
        }
        unauthorizedResult = null!;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
