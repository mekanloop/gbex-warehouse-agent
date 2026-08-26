using Gbex.EasyCube.Simulator;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ScenarioState>();
var app = builder.Build();

// --- Simulator control (test-only, not part of the real device's API) ---
app.MapPost("/simulator/configure", (ScenarioState state, ScenarioConfig config) =>
{
    state.Scenario = config.Scenario;
    if (config.ExpectedBarcode is not null) state.ExpectedBarcode = config.ExpectedBarcode;
    if (config.WeightKg is not null) state.WeightKg = config.WeightKg.Value;
    if (config.LengthCm is not null) state.LengthCm = config.LengthCm.Value;
    if (config.WidthCm is not null) state.WidthCm = config.WidthCm.Value;
    if (config.HeightCm is not null) state.HeightCm = config.HeightCm.Value;
    if (config.DelayedResponseTimeMs is not null) state.DelayedResponseTime = TimeSpan.FromMilliseconds(config.DelayedResponseTimeMs.Value);
    return Results.Ok(new { ok = true, scenario = state.Scenario.ToString() });
});

app.MapPost("/simulator/reset", (ScenarioState state) =>
{
    state.Scenario = SimulatorScenario.Healthy;
    return Results.Ok(new { ok = true });
});

// --- Real EasyCube Web API surface ---
app.MapGet("/devinfo", (ScenarioState state) =>
{
    if (state.Scenario == SimulatorScenario.UnhealthyDevice)
    {
        return Results.Json(new { error = "device not responding" }, statusCode: 500);
    }
    return Results.Ok(new
    {
        SerialNumber = state.DeviceId,
        DeviceModel = "EasyCube-1.6",
        Year = "2026",
        Sensor = "D415",
        SoftwareVersion = "3.0",
        MDMI = "sealed",
    });
});

app.MapGet("/errorlog", () => Results.Ok(new[]
{
    new { Datetime = "2026-08-27 10:00:00", Code = "sim01", Message = "simulator error log entry" },
}));

app.MapGet("/measure", (ScenarioState state) => Results.Redirect("/cap_measure"));
app.MapGet("/last_measure", (ScenarioState state) => Results.Redirect("/cap_measure"));
app.MapGet("/last_cap_measure", (ScenarioState state) => Results.Redirect("/cap_measure"));

app.MapGet("/cap_measure", async (ScenarioState state) =>
{
    switch (state.Scenario)
    {
        case SimulatorScenario.UnhealthyDevice:
            return Results.Json(new { error = "device not responding" }, statusCode: 500);

        case SimulatorScenario.DeviceErrorResponse:
            return Results.Json(new { error = "camera disconnected error!" }, statusCode: 500);

        case SimulatorScenario.MalformedResponse:
            return Results.Text("{ this is not valid json", "application/json");

        case SimulatorScenario.DelayedResponse:
            await Task.Delay(state.DelayedResponseTime);
            return Results.Ok(BuildMeasurement(state, timestampOverride: null));

        case SimulatorScenario.StaleMeasurement:
            return Results.Ok(BuildMeasurement(state, timestampOverride: DateTime.UtcNow.AddHours(-2)));

        case SimulatorScenario.WrongBarcode:
            return Results.Ok(BuildMeasurement(state, timestampOverride: null, barcodeOverride: "GBEX0000000000"));

        case SimulatorScenario.MismatchMeasurement:
            return Results.Ok(BuildMeasurement(state, timestampOverride: null, weightOverride: state.WeightKg * 3));

        default:
            return Results.Ok(BuildMeasurement(state, timestampOverride: null));
    }
});

app.MapGet("/alibi/{packageNumber}", (ScenarioState state, string packageNumber) =>
{
    return Results.Ok(BuildMeasurement(state, timestampOverride: null, packageNumberOverride: packageNumber));
});

app.MapGet("/image", (ScenarioState state) => Results.Ok(new { ImgBase64 = state.ImageBase64 }));

app.MapGet("/scale", () => Results.Ok(new { Enabled = false, ScaleType = 0, SerialPort = "/dev/ttyUSB0", Baudrate = 115200 }));
app.MapGet("/tare", () => Results.Ok(new { Enabled = false, Height = 3.1 }));

app.Run();

object BuildMeasurement(
    ScenarioState state,
    DateTime? timestampOverride,
    string? barcodeOverride = null,
    double? weightOverride = null,
    string? packageNumberOverride = null)
{
    var timestamp = timestampOverride ?? DateTime.Now;
    return new
    {
        DevID = state.DeviceId,
        PackageNumber = packageNumberOverride ?? state.NewPackageNumber(),
        TimeStamp = timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
        PackageHeight = state.HeightCm,
        PackageHeightUnit = "cm",
        PackageLenght = state.LengthCm,
        PackageLenghtUnit = "cm",
        PackageWidth = state.WidthCm,
        PackageWidthUnit = "cm",
        PackageWeight = weightOverride ?? state.WeightKg,
        PackageWeightUnit = "kg",
        RealVolume = 0,
        RealVolumeUnit = "",
        DimWeight = 0,
        DimWeightUnit = "kg",
        DimWeightFactor = 0.2,
        DimWeightFactorUnit = "kg",
        DimWeightFactorType = 0,
        Barcode = barcodeOverride,
        TareEnabled = false,
        TareHeight = 0,
        TareHeightUnit = "cm",
        ImgBase64 = state.ImageBase64,
    };
}

public sealed record ScenarioConfig(
    SimulatorScenario Scenario,
    string? ExpectedBarcode = null,
    double? WeightKg = null,
    double? LengthCm = null,
    double? WidthCm = null,
    double? HeightCm = null,
    double? DelayedResponseTimeMs = null);

/// <summary>Exposed for WebApplicationFactory-style in-process integration tests.</summary>
public partial class Program;
