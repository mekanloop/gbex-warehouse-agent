namespace Gbex.Warehouse.Agent.Core.Idempotency;

/// <summary>
/// Generates exactly one Idempotency-Key per logical operation. The caller
/// is responsible for persisting the returned key (in the outbox) and
/// reusing it on every retry — this class only produces a fresh key when
/// asked to; it does not itself remember anything, so accidentally calling
/// it twice for what should be the same logical operation is a caller bug,
/// not something this type can prevent. The workflow engine and outbox
/// enqueue path are the only two callers, and both generate the key exactly
/// once, before the first attempt, never again for a retry.
/// </summary>
public interface IIdempotencyKeyGenerator
{
    string NewKey();
}

public sealed class GuidIdempotencyKeyGenerator : IIdempotencyKeyGenerator
{
    public string NewKey() => $"agent_{Guid.NewGuid():N}";
}
