namespace Conduit.Durability.Tests.Support;

/// <summary>A scoped service whose <see cref="DisposeAsync"/> throws, so disposing the scope that holds it fails.</summary>
public sealed class ThrowingDisposeScopedService : IAsyncDisposable
{
    public ValueTask DisposeAsync() => throw new InvalidOperationException("Simulated scope disposal failure");
}
