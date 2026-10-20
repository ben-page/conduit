using Conduit.Handlers;
using Conduit.Messages;
using Conduit.TestUtils;

namespace Conduit.Tests.Support;

// --- Plain (inline) event with two throwing listeners and one that succeeds ---

public sealed record InlineFailureEvent(string Data) : IEvent;

// Wrapped so a test can prove GetBaseException() reaches the root cause.
public sealed class InlineRootCauseException(string message) : Exception(message);

public sealed class InlineFailingListenerA(EventSpy<string> runs) : IEventListener<InlineFailureEvent>
{
    public Task HandleAsync(InlineFailureEvent @event, CancellationToken cancellationToken)
    {
        runs.Record(nameof(InlineFailingListenerA));
        throw new InvalidOperationException("listener A failed", new InlineRootCauseException("root of A"));
    }
}

public sealed class InlineFailingListenerB(EventSpy<string> runs) : IEventListener<InlineFailureEvent>
{
    public Task HandleAsync(InlineFailureEvent @event, CancellationToken cancellationToken)
    {
        runs.Record(nameof(InlineFailingListenerB));
        throw new ArgumentException("listener B failed");
    }
}

public sealed class InlinePassingListener(EventSpy<string> runs) : IEventListener<InlineFailureEvent>
{
    public Task HandleAsync(InlineFailureEvent @event, CancellationToken cancellationToken)
    {
        runs.Record(nameof(InlinePassingListener));
        return Task.CompletedTask;
    }
}
