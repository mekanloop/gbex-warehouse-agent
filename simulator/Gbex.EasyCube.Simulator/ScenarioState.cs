namespace Gbex.EasyCube.Simulator;

public enum SimulatorScenario
{
    Healthy,
    UnhealthyDevice,
    NormalMeasurement,
    MismatchMeasurement,
    MalformedResponse,
    DelayedResponse,
    StaleMeasurement,
    WrongBarcode,
    DuplicatePackageNumber,
    DeviceErrorResponse,
}

/// <summary>Mutable, in-process state a test controls via POST /simulator/configure — this is a TEST TOOL, never production code, and lives only in this simulator project.</summary>
public sealed class ScenarioState
{
    public SimulatorScenario Scenario { get; set; } = SimulatorScenario.Healthy;
    public string DeviceId { get; set; } = "00000000";
    public string ExpectedBarcode { get; set; } = "GBEX2508230001";
    public double WeightKg { get; set; } = 5.0;
    public double LengthCm { get; set; } = 40.0;
    public double WidthCm { get; set; } = 30.0;
    public double HeightCm { get; set; } = 20.0;
    public TimeSpan DelayedResponseTime { get; set; } = TimeSpan.FromSeconds(30);
    public int NextPackageNumber { get; set; } = 1000;
    private int _fixedDuplicatePackageNumber = 4242;

    public string ImageBase64 { get; } =
        // A minimal, real 1x1 JPEG so evidence-upload paths have valid bytes to work with.
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGCQgKCgkICQkKDA8MCgsOCwkJDRENDg8QEBEQCgwSExIQEw8QEBD/2wBDAQMDAwQDBAgEBAgQCwkLEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBD/wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAj/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAAAAX/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwCdABmX/9k=";

    public string NewPackageNumber()
    {
        if (Scenario == SimulatorScenario.DuplicatePackageNumber)
        {
            return _fixedDuplicatePackageNumber.ToString();
        }
        return (NextPackageNumber++).ToString();
    }
}
