using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Durability.Tests.Support;

/// <summary>An event whose listener throws on its first <paramref name="Failures"/> deliveries.</summary>
public record FlakyInboxEvent(int Failures) : IEvent;

/// <summary>Counts each delivery and fails while failures remain.</summary>
public class FlakyInboxEventListener(DeliveryCounter deliveries) : IEventListener<FlakyInboxEvent>
{
    public Task HandleAsync(FlakyInboxEvent @event, CancellationToken cancellationToken)
    {
        var delivery = deliveries.Next();
        if (delivery <= @event.Failures)
        {
            throw new InvalidOperationException($"{nameof(FlakyInboxEvent)} failed on delivery {delivery}");
        }

        return Task.CompletedTask;
    }
}
