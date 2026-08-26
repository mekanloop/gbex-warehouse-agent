namespace Gbex.Warehouse.Agent.Infrastructure.Retry;

/// <summary>Exponential backoff with jitter — shared by the heartbeat service and the outbox processor so both back off the same way.</summary>
public static class BackoffPolicy
{
    public static TimeSpan Compute(int attemptNumber, TimeSpan baseDelay, TimeSpan maxDelay, Random random)
    {
        if (attemptNumber < 1) attemptNumber = 1;
        var exponent = Math.Min(attemptNumber - 1, 10); // cap the exponent so this never overflows
        var raw = baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(raw, maxDelay.TotalMilliseconds);
        // Full jitter: uniformly random between 0 and the capped delay —
        // avoids every Agent instance retrying in lockstep after an outage.
        var jittered = random.NextDouble() * capped;
        return TimeSpan.FromMilliseconds(jittered);
    }
}
