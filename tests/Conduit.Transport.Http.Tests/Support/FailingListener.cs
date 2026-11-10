using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// A listener that throws inside its own try, so the dispatch fails with an <c>EventListenerException</c> whose
/// first failure is this one.
/// </summary>
public sealed class FailingListener : IEventListener<ListenerFailureEvent>
{
    public Task HandleAsync(ListenerFailureEvent @event, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The listener failed.");
}
