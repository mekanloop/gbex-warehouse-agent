using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Correlation;
using Gbex.Warehouse.Agent.Core.Models;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

public class MeasurementCorrelationValidatorTests
{
    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private static CapturedMeasurement Measurement(DateTimeOffset timestamp, string packageNumber = "419", string? deviceBarcode = null) => new()
    {
        DeviceId = "00000000",
        PackageNumber = packageNumber,
        Timestamp = timestamp,
        WeightKg = 5,
        LengthCm = 40,
        WidthCm = 30,
        HeightCm = 20,
        DeviceReportedBarcode = deviceBarcode,
    };

    [Fact]
    public void Validate_accepts_a_fresh_correlated_measurement()
    {
        var clock = new FakeClock();
        var validator = new MeasurementCorrelationValidator(clock, TimeSpan.FromSeconds(30));

        var result = validator.Validate("GBEX2508230001", Measurement(clock.UtcNow));

        Assert.IsType<CorrelationResult.Valid>(result);
    }

    [Fact]
    public void Validate_rejects_a_stale_measurement()
    {
        var clock = new FakeClock();
        var validator = new MeasurementCorrelationValidator(clock, TimeSpan.FromSeconds(30));

        var stale = Measurement(clock.UtcNow.AddMinutes(-5));
        var result = validator.Validate("GBEX2508230001", stale);

        Assert.IsType<CorrelationResult.StaleMeasurement>(result);
    }

    [Fact]
    public void Validate_rejects_a_measurement_whose_package_number_was_already_consumed()
    {
        var clock = new FakeClock();
        var validator = new MeasurementCorrelationValidator(clock, TimeSpan.FromSeconds(30));
        validator.MarkConsumed("419");

        var result = validator.Validate("GBEX2508230001", Measurement(clock.UtcNow, packageNumber: "419"));

        Assert.IsType<CorrelationResult.PackageNumberAlreadyUsed>(result);
    }

    [Fact]
    public void Validate_does_not_silently_reuse_the_previous_packages_measurement_for_a_new_scan()
    {
        var clock = new FakeClock();
        var validator = new MeasurementCorrelationValidator(clock, TimeSpan.FromSeconds(30));

        var first = Measurement(clock.UtcNow, packageNumber: "100");
        Assert.IsType<CorrelationResult.Valid>(validator.Validate("GBEX2508230001", first));
        validator.MarkConsumed("100");

        // A NEW scan (different barcode) but the device reports the SAME
        // package number again (device didn't advance) — must be rejected,
        // not silently accepted as if it were a fresh reading.
        var stalePackageAgain = Measurement(clock.UtcNow, packageNumber: "100");
        var result = validator.Validate("GBEX2508230002", stalePackageAgain);

        Assert.IsType<CorrelationResult.PackageNumberAlreadyUsed>(result);
    }

    [Fact]
    public void Validate_rejects_when_the_device_reported_barcode_does_not_match_the_scanned_barcode()
    {
        var clock = new FakeClock();
        var validator = new MeasurementCorrelationValidator(clock, TimeSpan.FromSeconds(30));

        var wrongBarcode = Measurement(clock.UtcNow, deviceBarcode: "GBEX0000000000");
        var result = validator.Validate("GBEX2508230001", wrongBarcode);

        Assert.IsType<CorrelationResult.DeviceBarcodeMismatch>(result);
    }

    [Fact]
    public void Validate_ignores_device_barcode_check_when_device_reported_none()
    {
        var clock = new FakeClock();
        var validator = new MeasurementCorrelationValidator(clock, TimeSpan.FromSeconds(30));

        var noBarcode = Measurement(clock.UtcNow, deviceBarcode: null);
        var result = validator.Validate("GBEX2508230001", noBarcode);

        Assert.IsType<CorrelationResult.Valid>(result);
    }

    [Fact]
    public void Validate_rejects_missing_package_number()
    {
        var clock = new FakeClock();
        var validator = new MeasurementCorrelationValidator(clock, TimeSpan.FromSeconds(30));

        var missing = Measurement(clock.UtcNow, packageNumber: "");
        var result = validator.Validate("GBEX2508230001", missing);

        Assert.IsType<CorrelationResult.MissingPackageNumber>(result);
    }
}
