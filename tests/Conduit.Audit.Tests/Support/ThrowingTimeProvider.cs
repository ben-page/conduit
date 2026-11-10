namespace Conduit.Audit.Tests.Support;

/// <summary>A clock that throws on every reading: whatever reads it fails, as a faulting sweep does.</summary>
public sealed class ThrowingTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("the clock is broken");
}
