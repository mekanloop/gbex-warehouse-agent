using System.Net;
using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Infrastructure.EasyCube;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

/// <summary>
/// Verifies EasyCubeClient correctly deserializes the device's REAL field
/// names (including the "PackageLenght" spelling) and converts to GBEX's
/// expected KG/CM — this is the exact JSON shape transcribed from the
/// manufacturer's own /cap_measure example response.
/// </summary>
public class EasyCubeClientMappingTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public FakeHandler(HttpStatusCode status, string body) { _status = status; _body = body; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
    }

    private static EasyCubeClient BuildClient(HttpStatusCode status, string body)
    {
        var httpClient = new HttpClient(new FakeHandler(status, body));
        var options = Options.Create(new EasyCubeOptions { BaseUrl = "http://localhost:8080" });
        return new EasyCubeClient(httpClient, options, NullLogger<EasyCubeClient>.Instance);
    }

    private const string RealCapMeasureExample = """
        {
          "DevID": "00000000",
          "PackageNumber": "410",
          "TimeStamp": "2021-07-26 16:06:27",
          "PackageHeight": 23.5,
          "PackageHeightUnit": "cm",
          "PackageLenght": 17.2,
          "PackageLenghtUnit": "cm",
          "PackageWidth": 8.7,
          "PackageWidthUnit": "cm",
          "PackageWeight": 3.552,
          "PackageWeightUnit": "kg",
          "RealVolume": 0,
          "RealVolumeUnit": "",
          "DimWeight": 0,
          "DimWeightUnit": "kg",
          "DimWeightFactor": 0.877,
          "DimWeightFactorUnit": "kg",
          "DimWeightFactorType": 0,
          "Barcode": null,
          "TareEnabled": false,
          "TareHeight": 0,
          "TareHeightUnit": "DOM",
          "ImgBase64": "aGVsbG8="
        }
        """;

    [Fact]
    public async Task CaptureMeasurementAsync_maps_the_real_device_field_names_correctly()
    {
        var client = BuildClient(HttpStatusCode.OK, RealCapMeasureExample);

        var result = await client.CaptureMeasurementAsync(CancellationToken.None);

        var ok = Assert.IsType<MeasurementOutcome>(result);
        var m = ok.Measurement!;
        Assert.Equal("00000000", m.DeviceId);
        Assert.Equal("410", m.PackageNumber);
        Assert.Equal(3.552m, m.WeightKg);
        Assert.Equal(17.2m, m.LengthCm); // mapped from "PackageLenght", not "PackageLength"
        Assert.Equal(8.7m, m.WidthCm);
        Assert.Equal(23.5m, m.HeightCm);
        Assert.Equal("aGVsbG8=", m.ImageBase64);
        Assert.Null(m.DeviceReportedBarcode);
    }

    [Fact]
    public async Task GetDeviceInfoAsync_maps_devinfo_fields()
    {
        const string body = """
            { "SerialNumber": "00000000", "DeviceModel": "EasyCube-1.6", "Year": "2021", "Sensor": "D415", "SoftwareVersion": "3.0", "MDMI": "sealed" }
            """;
        var client = BuildClient(HttpStatusCode.OK, body);

        var result = await client.GetDeviceInfoAsync(CancellationToken.None);

        var health = Assert.IsType<DeviceHealth>(result);
        Assert.Equal("EasyCube-1.6", health.Info!.DeviceModel);
    }

    [Fact]
    public async Task A_malformed_JSON_response_is_reported_as_MalformedResponse_not_a_crash()
    {
        var client = BuildClient(HttpStatusCode.OK, "{ this is not valid json");

        var result = await client.CaptureMeasurementAsync(CancellationToken.None);

        Assert.IsType<EasyCubeResult.MalformedResponse>(result);
    }

    [Fact]
    public async Task A_device_error_response_is_surfaced_as_a_DeviceError_not_a_crash()
    {
        var client = BuildClient(HttpStatusCode.InternalServerError, "{\"error\":\"camera disconnected error!\"}");

        var result = await client.CaptureMeasurementAsync(CancellationToken.None);

        var error = Assert.IsType<EasyCubeResult.DeviceError>(result);
        Assert.Contains("camera disconnected", error.Message);
    }

    [Fact]
    public async Task An_unrecognized_weight_unit_is_rejected_rather_than_silently_misinterpreted()
    {
        // Matches the manufacturer's own (almost certainly erroneous)
        // documentation example where PackageWeightUnit was "cm3" — this
        // must be surfaced as an error, never silently treated as kg.
        var body = RealCapMeasureExample.Replace("\"PackageWeightUnit\": \"kg\"", "\"PackageWeightUnit\": \"cm3\"");
        var client = BuildClient(HttpStatusCode.OK, body);

        var result = await client.CaptureMeasurementAsync(CancellationToken.None);

        Assert.IsType<EasyCubeResult.MalformedResponse>(result);
    }
}
