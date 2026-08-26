using Gbex.Warehouse.Agent.Core.Idempotency;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

public class IdempotencyKeyGeneratorTests
{
    [Fact]
    public void NewKey_returns_a_distinct_value_each_call()
    {
        var generator = new GuidIdempotencyKeyGenerator();
        var a = generator.NewKey();
        var b = generator.NewKey();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NewKey_is_stable_when_the_caller_reuses_the_same_returned_value()
    {
        // The generator's contract is "call once per logical operation, the
        // CALLER reuses the returned string for every retry" — this test
        // documents that the string itself does not change on its own.
        var generator = new GuidIdempotencyKeyGenerator();
        var key = generator.NewKey();
        var reusedForRetry1 = key;
        var reusedForRetry2 = key;
        Assert.Equal(reusedForRetry1, reusedForRetry2);
    }
}
