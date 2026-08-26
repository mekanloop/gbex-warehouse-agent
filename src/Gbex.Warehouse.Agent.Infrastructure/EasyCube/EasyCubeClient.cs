using System.Globalization;
using System.Text.Json;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Models;
using Gbex.Warehouse.Agent.Core.Units;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gbex.Warehouse.Agent.Infrastructure.EasyCube;

/// <summary>
/// HTTP implementation of IEasyCubeClient against the manufacturer's
/// documented EasyCube Web API (see docs/EASYCUBE_CONTRACT.md). All HTTP
/// calls to the device live here — the workflow engine and WPF UI never
/// construct a request to the device themselves.
///
/// ASSUMPTION FLAGGED FOR PHYSICAL-HARDWARE VERIFICATION: the device's
/// TimeStamp field ("2021-07-26 16:18:04" in the manufacturer's own
/// example) carries no explicit timezone. This client parses it as the
/// Agent PC's LOCAL time (the device sits on the same warehouse LAN/power
/// as the Agent, and /datetime's own default is "AutoDatetime": true), then
/// converts to a UTC-based DateTimeOffset for staleness comparison. Verify
/// this assumption against the real device before trusting staleness
/// rejection in production.
/// </summary>
public sealed class EasyCubeClient : IEasyCubeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<EasyCubeClient> _logger;

    public EasyCubeClient(HttpClient http, IOptions<EasyCubeOptions> options, ILogger<EasyCubeClient> logger)
    {
        var opts = options.Value;
        _http = http;
        _http.BaseAddress = new Uri(opts.BaseUrl, UriKind.Absolute);
        _http.Timeout = opts.RequestTimeout;
        _logger = logger;
    }

    public async Task<EasyCubeResult> GetDeviceInfoAsync(CancellationToken ct)
    {
        return await GetAsync<EasyCubeDeviceInfoResponse>("/devinfo", ct, body =>
        {
            if (body.SerialNumber is null || body.DeviceModel is null)
            {
                return new EasyCubeResult.MalformedResponse("devinfo missing required fields");
            }

            return DeviceHealth.Healthy(new DeviceInfo
            {
                SerialNumber = body.SerialNumber,
                DeviceModel = body.DeviceModel,
                Year = body.Year ?? "",
                Sensor = body.Sensor ?? "",
                SoftwareVersion = body.SoftwareVersion ?? "",
                Mdmi = body.Mdmi ?? "",
            });
        });
    }

    public async Task<EasyCubeResult> GetErrorLogAsync(CancellationToken ct)
    {
        return await GetAsync<EasyCubeErrorLogEntry[]>("/errorlog", ct, _ => new EasyCubeResult.Success());
    }

    public Task<EasyCubeResult> CaptureMeasurementAsync(CancellationToken ct) =>
        GetMeasurementAsync("/cap_measure", ct);

    public Task<EasyCubeResult> GetLastMeasurementAsync(CancellationToken ct) =>
        GetMeasurementAsync("/last_measure", ct);

    public Task<EasyCubeResult> GetByPackageNumberAsync(string packageNumber, CancellationToken ct) =>
        GetMeasurementAsync($"/alibi/{Uri.EscapeDataString(packageNumber)}", ct);

    private async Task<EasyCubeResult> GetMeasurementAsync(string path, CancellationToken ct)
    {
        return await GetAsync<EasyCubeMeasurementResponse>(path, ct, body =>
        {
            if (string.IsNullOrWhiteSpace(body.DevId) || string.IsNullOrWhiteSpace(body.PackageNumber))
            {
                return new EasyCubeResult.MalformedResponse($"{path}: missing DevID/PackageNumber");
            }

            var weight = UnitConverter.ParseWeightToKg((decimal)body.PackageWeight, body.PackageWeightUnit);
            if (weight is not UnitParseResult.Ok weightOk)
            {
                return new EasyCubeResult.MalformedResponse($"{path}: unreadable weight ({DescribeUnitFailure(weight)})");
            }

            var length = UnitConverter.ParseLengthToCm((decimal)body.PackageLenght, body.PackageLenghtUnit);
            var width = UnitConverter.ParseLengthToCm((decimal)body.PackageWidth, body.PackageWidthUnit);
            var height = UnitConverter.ParseLengthToCm((decimal)body.PackageHeight, body.PackageHeightUnit);
            if (length is not UnitParseResult.Ok lengthOk || width is not UnitParseResult.Ok widthOk || height is not UnitParseResult.Ok heightOk)
            {
                return new EasyCubeResult.MalformedResponse(
                    $"{path}: unreadable dimensions (L={DescribeUnitFailure(length)}, W={DescribeUnitFailure(width)}, H={DescribeUnitFailure(height)})");
            }

            decimal? dimWeightKg = null;
            if (body.DimWeight > 0)
            {
                var dimWeight = UnitConverter.ParseWeightToKg((decimal)body.DimWeight, body.DimWeightUnit);
                if (dimWeight is UnitParseResult.Ok dimOk) dimWeightKg = dimOk.Value;
            }

            var timestamp = ParseDeviceTimestamp(body.TimeStamp);

            var measurement = new CapturedMeasurement
            {
                DeviceId = body.DevId,
                PackageNumber = body.PackageNumber,
                Timestamp = timestamp,
                WeightKg = weightOk.Value,
                LengthCm = lengthOk.Value,
                WidthCm = widthOk.Value,
                HeightCm = heightOk.Value,
                DimensionalWeightKg = dimWeightKg,
                DeviceReportedBarcode = string.IsNullOrWhiteSpace(body.Barcode) ? null : body.Barcode,
                ImageBase64 = body.ImgBase64,
            };

            return MeasurementOutcome.Ok(measurement);
        });
    }

    private static string DescribeUnitFailure(UnitParseResult result) => result switch
    {
        UnitParseResult.UnrecognizedUnit u => $"unrecognized unit '{u.Unit}'",
        UnitParseResult.OutOfRange r => $"out of range ({r.Value})",
        UnitParseResult.InvalidNumber n => $"invalid number '{n.RawValue}'",
        _ => "unknown",
    };

    /// <summary>Invariant-culture parse of the device's "yyyy-MM-dd HH:mm:ss" timestamp — see class doc for the local-time assumption this makes.</summary>
    private DateTimeOffset ParseDeviceTimestamp(string? raw)
    {
        if (raw is not null && DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return new DateTimeOffset(parsed.ToUniversalTime());
        }

        _logger.LogWarning("EasyCube timestamp '{Raw}' did not parse — treating capture as 'now' (staleness check effectively skipped)", raw);
        return DateTimeOffset.UtcNow;
    }

    private async Task<EasyCubeResult> GetAsync<T>(string path, CancellationToken ct, Func<T, EasyCubeResult> onSuccess) where T : class
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(path, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("EasyCube request to {Path} timed out", path);
            return new EasyCubeResult.Timeout();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("EasyCube request to {Path} unreachable: {ErrorType}", path, ex.GetType().Name);
            return new EasyCubeResult.Unreachable(ex.GetType().Name);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await TryReadAsync<EasyCubeErrorResponse>(response, ct);
                return new EasyCubeResult.DeviceError(((int)response.StatusCode).ToString(), errorBody?.Error ?? "device returned an error");
            }

            var body = await TryReadAsync<T>(response, ct);
            if (body is null)
            {
                return new EasyCubeResult.MalformedResponse($"{path}: could not deserialize response");
            }

            try
            {
                return onSuccess(body);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "EasyCube response mapping threw for {Path}", path);
                return new EasyCubeResult.MalformedResponse($"{path}: mapping error");
            }
        }
    }

    private static async Task<T?> TryReadAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : class
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
}
