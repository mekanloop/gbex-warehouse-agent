using Gbex.Warehouse.Agent.Core.Abstractions;
using Gbex.Warehouse.Agent.Core.Models;

namespace Gbex.Warehouse.Agent.Core.Correlation;

public abstract record CorrelationResult
{
    public sealed record Valid : CorrelationResult;
    public sealed record StaleMeasurement(DateTimeOffset MeasuredAt, TimeSpan Age) : CorrelationResult;
    public sealed record PackageNumberAlreadyUsed(string PackageNumber) : CorrelationResult;
    public sealed record DeviceBarcodeMismatch(string Scanned, string DeviceReported) : CorrelationResult;
    public sealed record MissingPackageNumber : CorrelationResult;
}

/// <summary>
/// Validates that a captured EasyCube measurement genuinely corresponds to
/// the barcode the operator just scanned — before it is ever submitted to
/// GBEX. Three independent checks, all must pass:
///   1. Not stale (captured within a bounded window of "now").
///   2. Package number has not already been consumed for a DIFFERENT scan
///      in this session (never silently reuse the previous package's
///      measurement just because the device didn't report a new one yet).
///   3. If the device itself reported a barcode (barcode-correlation mode),
///      it must match what the operator scanned.
/// </summary>
public sealed class MeasurementCorrelationValidator
{
    private readonly IClock _clock;
    private readonly TimeSpan _maxAge;
    private readonly HashSet<string> _consumedPackageNumbers = new();

    public MeasurementCorrelationValidator(IClock clock, TimeSpan? maxAge = null)
    {
        _clock = clock;
        _maxAge = maxAge ?? TimeSpan.FromSeconds(30);
    }

    public CorrelationResult Validate(string scannedBarcode, CapturedMeasurement measurement)
    {
        if (string.IsNullOrWhiteSpace(measurement.PackageNumber))
        {
            return new CorrelationResult.MissingPackageNumber();
        }

        var age = _clock.UtcNow - measurement.Timestamp;
        if (age > _maxAge || age < TimeSpan.Zero)
        {
            return new CorrelationResult.StaleMeasurement(measurement.Timestamp, age);
        }

        if (_consumedPackageNumbers.Contains(measurement.PackageNumber))
        {
            return new CorrelationResult.PackageNumberAlreadyUsed(measurement.PackageNumber);
        }

        if (!string.IsNullOrWhiteSpace(measurement.DeviceReportedBarcode)
            && !string.Equals(measurement.DeviceReportedBarcode, scannedBarcode, StringComparison.OrdinalIgnoreCase))
        {
            return new CorrelationResult.DeviceBarcodeMismatch(scannedBarcode, measurement.DeviceReportedBarcode);
        }

        return new CorrelationResult.Valid();
    }

    /// <summary>Call only after a measurement has been successfully validated AND submitted — marks its package number as consumed so a subsequent stale re-read of the "last" measurement cannot be silently resubmitted for a different scan.</summary>
    public void MarkConsumed(string packageNumber) => _consumedPackageNumbers.Add(packageNumber);
}
