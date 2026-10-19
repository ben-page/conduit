namespace Conduit.Tests.Support;

/// <summary>A scoped service for the dispatch retry tests: its id names the scope that resolved it.</summary>
public sealed class AttemptProbe : IDisposable
{
    public Guid Id { get; } = Guid.NewGuid();

    public bool Disposed { get; private set; }

    public void Dispose()
    {
        this.Disposed = true;
    }
}
