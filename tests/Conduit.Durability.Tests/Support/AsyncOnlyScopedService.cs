namespace Conduit.Durability.Tests.Support;

/// <summary>
/// A scoped service that implements only <see cref="IAsyncDisposable"/>, as some database contexts and pooled clients
/// do: <c>IServiceScope.Dispose()</c> throws <see cref="InvalidOperationException"/> for a scope holding one, so the
/// scope must be disposed asynchronously.
/// </summary>
public sealed class AsyncOnlyScopedService : IAsyncDisposable
{
    private int disposed;

    /// <summary>Whether its scope disposed it.</summary>
    public bool Disposed => Volatile.Read(ref this.disposed) != 0;

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref this.disposed, 1);
        return ValueTask.CompletedTask;
    }
}
