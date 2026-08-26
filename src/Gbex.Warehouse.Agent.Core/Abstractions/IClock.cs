namespace Gbex.Warehouse.Agent.Core.Abstractions;

/// <summary>Testable clock — outbox scheduling and backoff timing tests need to control "now" deterministically.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
