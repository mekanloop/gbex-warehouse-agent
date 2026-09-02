using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Models;
using Gbex.Warehouse.Agent.Core.Update;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gbex.Warehouse.Agent.Infrastructure.Gbex;

/// <summary>
/// Typed HTTP client for GBEX's machine-authenticated warehouse routes.
/// Exact contracts from app/api/warehouse/* in the gbex website repo (read
/// read-only, never modified):
///   POST /api/warehouse/heartbeat          { agentVersion? }
///   POST /api/warehouse/orders/lookup      { barcode }
///   POST /api/warehouse/measurements       raw hardware facts + Idempotency-Key
///   POST /api/warehouse/measurements/{id}/evidence   multipart "photo" + Idempotency-Key
///   GET  /api/warehouse/agent-version                self-update manifest check
///   GET  /api/warehouse/agent-version/download        installer bytes (verify sha256 before running)
///
/// Deserializes ONLY the minimal station-safe DTO — never a raw JSON blob
/// that could carry an unexpected field through untouched. Never logs the
/// Authorization header or any response body verbatim (only status codes
/// and known-safe field names).
///
/// KNOWN BACKEND GAP (see docs/BACKEND_CHANGES_NEEDED.md): the current
/// backend's requireStationScope returns the SAME 401 body for an invalid
/// token and for a disabled station — there is no way for this client to
/// distinguish "revoked" from "disabled" today. This client treats both as
/// Unauthorized until the backend is changed to disambiguate; StationDisabled
/// is defined and wired through the Core contract for when that lands, but
/// nothing in the current backend response can trigger it yet.
/// </summary>
public sealed class GbexApiClient : IGbexApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ISecretStore _secretStore;
    private readonly ILogger<GbexApiClient> _logger;

    public GbexApiClient(HttpClient http, IOptions<GbexApiOptions> options, ISecretStore secretStore, ILogger<GbexApiClient> logger)
    {
        var opts = options.Value;
        var baseUri = new Uri(opts.BaseUrl, UriKind.Absolute);
        var isLoopback = Uri.CheckHostName(baseUri.Host) != UriHostNameType.Unknown
            && (baseUri.Host is "localhost" or "127.0.0.1" or "::1");

        if (baseUri.Scheme != Uri.UriSchemeHttps && !(opts.AllowInsecureForDevelopment && isLoopback))
        {
            throw new InvalidOperationException(
                $"GBEX API base URL must be HTTPS outside development (got '{baseUri.Scheme}' for host '{baseUri.Host}'). " +
                "Set AllowInsecureForDevelopment only for a loopback development server.");
        }

        _http = http;
        _http.BaseAddress = baseUri;
        _http.Timeout = opts.RequestTimeout;
        _secretStore = secretStore;
        _logger = logger;
    }

    public async Task<GbexApiResult> HeartbeatAsync(string agentVersion, CancellationToken ct)
    {
        using var request = await BuildRequestAsync(HttpMethod.Post, "/api/warehouse/heartbeat", new { agentVersion }, ct);
        if (request is null) return new GbexApiResult.Unauthorized();

        return await SendAsync(request, ct, async response =>
        {
            var body = await ReadJsonAsync<HeartbeatResponse>(response, ct);
            return body is null ? new GbexApiResult.TransientFailure("Malformed heartbeat response") : HeartbeatOutcome.Ok(body.Station ?? "");
        });
    }

    public async Task<GbexApiResult> LookupOrderAsync(string barcode, CancellationToken ct)
    {
        using var request = await BuildRequestAsync(HttpMethod.Post, "/api/warehouse/orders/lookup", new { barcode }, ct);
        if (request is null) return new GbexApiResult.Unauthorized();

        return await SendAsync(request, ct, async response =>
        {
            var body = await ReadJsonAsync<OrderLookupResponse>(response, ct);
            if (body?.Order is null) return new GbexApiResult.TransientFailure("Malformed order lookup response");

            var dto = new StationOrderDto
            {
                Id = body.Order.Id,
                GbexBarcode = body.Order.GbexBarcode,
                Status = body.Order.Status,
                DestinationCountry = body.Order.DestinationCountry,
                DestinationCity = body.Order.DestinationCity,
                DeclaredWeight = body.Order.DeclaredWeight,
                DeclaredDesi = body.Order.DeclaredDesi,
                DeclaredLength = body.Order.DeclaredLength,
                DeclaredWidth = body.Order.DeclaredWidth,
                DeclaredHeight = body.Order.DeclaredHeight,
                FulfillmentMode = body.Order.FulfillmentMode,
                RequiresManualCarrierLabel = body.Order.RequiresManualCarrierLabel,
                ManualFulfillmentStatus = body.Order.ManualFulfillmentStatus,
            };
            return OrderLookupOutcome.Ok(dto);
        });
    }

    public async Task<GbexApiResult> SubmitMeasurementAsync(MeasurementSubmission submission, string idempotencyKey, CancellationToken ct)
    {
        var payload = new
        {
            barcode = submission.Barcode,
            weightKg = submission.WeightKg,
            lengthCm = submission.LengthCm,
            widthCm = submission.WidthCm,
            heightCm = submission.HeightCm,
            dimensionalWeightKg = submission.DimensionalWeightKg,
            deviceId = submission.DeviceId,
            packageNumber = submission.PackageNumber,
        };

        using var request = await BuildRequestAsync(HttpMethod.Post, "/api/warehouse/measurements", payload, ct, idempotencyKey);
        if (request is null) return new GbexApiResult.Unauthorized();

        return await SendAsync(request, ct, async response =>
        {
            var body = await ReadJsonAsync<MeasurementSubmitResponse>(response, ct);
            if (body is null) return new GbexApiResult.TransientFailure("Malformed measurement response");

            var result = new MeasurementSubmissionResult
            {
                MeasurementId = body.MeasurementId,
                Result = body.Result == "mismatch" ? MeasurementResultKind.Mismatch : MeasurementResultKind.Pass,
                RequiresEvidence = body.RequiresEvidence,
            };
            return MeasurementSubmitOutcome.Ok(result);
        });
    }

    public async Task<GbexApiResult> UploadEvidenceAsync(string measurementId, byte[] imageBytes, string mimeType, string idempotencyKey, CancellationToken ct)
    {
        var secret = await _secretStore.TryGetStationSecretAsync(ct);
        if (secret is null) return new GbexApiResult.Unauthorized();

        using var content = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        content.Add(imageContent, "photo", "evidence.jpg");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/warehouse/measurements/{Uri.EscapeDataString(measurementId)}/evidence")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return await SendAsync(request, ct, async response =>
        {
            var body = await ReadJsonAsync<EvidenceUploadResponse>(response, ct);
            return body?.PhotoUrl is null
                ? new GbexApiResult.TransientFailure("Malformed evidence upload response")
                : EvidenceUploadOutcome.Ok(body.PhotoUrl);
        });
    }

    public async Task<GbexApiResult> CheckForUpdateAsync(CancellationToken ct)
    {
        var secret = await _secretStore.TryGetStationSecretAsync(ct);
        if (secret is null) return new GbexApiResult.Unauthorized();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/warehouse/agent-version");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        return await SendAsync(request, ct, async response =>
        {
            var body = await ReadJsonAsync<AgentVersionResponse>(response, ct);
            if (body is null) return new GbexApiResult.TransientFailure("Malformed agent-version response");

            if (!body.Available || body.LatestVersion is null || body.InstallerUrl is null || body.Sha256 is null)
            {
                return AgentUpdateCheckOutcome.NoneAvailable();
            }

            return AgentUpdateCheckOutcome.Available(new AgentUpdateManifest
            {
                LatestVersion = body.LatestVersion,
                InstallerUrl = body.InstallerUrl,
                Sha256 = body.Sha256,
                ReleaseNotes = body.ReleaseNotes,
                Mandatory = body.Mandatory,
            });
        });
    }

    /// <summary>
    /// Not built on top of the shared SendAsync/onSuccess pipeline — that
    /// path assumes a JSON response body, but an installer download is raw
    /// binary streamed straight to disk. Uses
    /// HttpCompletionOption.ResponseHeadersRead so a large file is never
    /// buffered whole in memory before the copy even starts.
    /// </summary>
    public async Task<GbexApiResult> DownloadUpdateInstallerAsync(string installerUrl, string destinationPath, CancellationToken ct)
    {
        var secret = await _secretStore.TryGetStationSecretAsync(ct);
        if (secret is null) return new GbexApiResult.Unauthorized();

        using var request = new HttpRequestMessage(HttpMethod.Get, installerUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("GBEX update download timed out");
            return new GbexApiResult.TransientFailure("timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("GBEX update download failed: {ErrorType}", ex.GetType().Name);
            return new GbexApiResult.TransientFailure("network");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new GbexApiResult.Unauthorized(),
                    HttpStatusCode.NotFound => new GbexApiResult.NotFound("Yayınlanmış bir sürüm yok."),
                    _ => new GbexApiResult.TransientFailure($"http_{(int)response.StatusCode}"),
                };
            }

            try
            {
                await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await httpStream.CopyToAsync(fileStream, ct);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Writing downloaded update installer failed");
                return new GbexApiResult.TransientFailure("io");
            }

            return new GbexApiResult.Success();
        }
    }

    private async Task<HttpRequestMessage?> BuildRequestAsync(HttpMethod method, string path, object payload, CancellationToken ct, string? idempotencyKey = null)
    {
        var secret = await _secretStore.TryGetStationSecretAsync(ct);
        if (secret is null) return null;

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return request;
    }

    private async Task<GbexApiResult> SendAsync(HttpRequestMessage request, CancellationToken ct, Func<HttpResponseMessage, Task<GbexApiResult>> onSuccess)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("GBEX request to {Path} timed out", request.RequestUri?.AbsolutePath);
            return new GbexApiResult.TransientFailure("timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("GBEX request to {Path} failed: {ErrorType}", request.RequestUri?.AbsolutePath, ex.GetType().Name);
            return new GbexApiResult.TransientFailure("network");
        }

        using (response)
        {
            _logger.LogDebug("GBEX {Method} {Path} -> {Status}", request.Method, request.RequestUri?.AbsolutePath, (int)response.StatusCode);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                case HttpStatusCode.Created:
                    return await onSuccess(response);
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    // See class doc: the current backend cannot distinguish
                    // "revoked token" from "disabled station" on this code
                    // path — both surface here as Unauthorized.
                    return new GbexApiResult.Unauthorized();
                case HttpStatusCode.NotFound:
                    return new GbexApiResult.NotFound(await SafeMessageAsync(response, ct));
                case HttpStatusCode.Conflict:
                    return new GbexApiResult.Conflict(await SafeMessageAsync(response, ct));
                case HttpStatusCode.UnprocessableEntity:
                    return new GbexApiResult.ValidationFailed(await SafeMessageAsync(response, ct));
                case HttpStatusCode.RequestTimeout:
                case HttpStatusCode.TooManyRequests:
                case HttpStatusCode.BadGateway:
                case HttpStatusCode.ServiceUnavailable:
                case HttpStatusCode.GatewayTimeout:
                    return new GbexApiResult.TransientFailure($"http_{(int)response.StatusCode}");
                default:
                    // Any other unexpected status (including a genuine 5xx
                    // not listed above) is treated as transient — safe to
                    // retry via the outbox rather than silently dropped.
                    return new GbexApiResult.TransientFailure($"http_{(int)response.StatusCode}");
            }
        }
    }

    private static async Task<string> SafeMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await ReadJsonAsync<ErrorResponse>(response, ct);
            return body?.Message ?? $"HTTP {(int)response.StatusCode}";
        }
        catch
        {
            return $"HTTP {(int)response.StatusCode}";
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : class
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ErrorResponse(string? Message);
    private sealed record HeartbeatResponse(bool Ok, string? Station);
    private sealed record OrderLookupResponse(StationOrderWire? Order);
    private sealed record StationOrderWire(
        string Id,
        string GbexBarcode,
        string Status,
        string DestinationCountry,
        string DestinationCity,
        decimal DeclaredWeight,
        decimal DeclaredDesi,
        decimal DeclaredLength,
        decimal DeclaredWidth,
        decimal DeclaredHeight,
        string FulfillmentMode,
        bool RequiresManualCarrierLabel,
        string? ManualFulfillmentStatus);
    private sealed record MeasurementSubmitResponse(string MeasurementId, string Result, bool RequiresEvidence);
    private sealed record EvidenceUploadResponse(bool Ok, string? PhotoUrl);
    private sealed record AgentVersionResponse(bool Available, string? LatestVersion, string? InstallerUrl, string? Sha256, string? ReleaseNotes, bool Mandatory);
}
