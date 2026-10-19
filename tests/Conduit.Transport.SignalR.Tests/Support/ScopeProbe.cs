namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>A scoped service for the dispatch retry tests: its id names the scope that resolved it.</summary>
public sealed class ScopeProbe
{
    public Guid Id { get; } = Guid.NewGuid();
}
