using Conduit.Handlers;

namespace Conduit.Tests.Support;

/// <summary>
/// A listener whose constructor throws, so resolving the listeners of <see cref="UnbuildableListenerEvent"/> fails
/// before any of them runs, outside every listener's own try.
/// </summary>
public sealed class UnbuildableListener : IEventListener<UnbuildableListenerEvent>
{
    public UnbuildableListener() => throw new InvalidOperationException("The listener cannot be built.");

    public Task HandleAsync(UnbuildableListenerEvent @event, CancellationToken cancellationToken) => Task.CompletedTask;
}
