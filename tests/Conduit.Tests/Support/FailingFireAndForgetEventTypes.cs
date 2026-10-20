using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;
using Conduit.TestUtils;

namespace Conduit.Tests.Support;

// --- Fire-and-forget event whose listener throws ---

[FireAndForget]
public sealed record FailingFireAndForgetEvent(string Data) : IEvent;

public sealed class FailingFireAndForgetEventListener(
    EventSpy<FailingFireAndForgetEvent> spy) : IEventListener<FailingFireAndForgetEvent>
{
    public Task HandleAsync(FailingFireAndForgetEvent @event, CancellationToken cancellationToken)
    {
        spy.Record(@event);
        throw new InvalidOperationException("background listener failed");
    }
}
