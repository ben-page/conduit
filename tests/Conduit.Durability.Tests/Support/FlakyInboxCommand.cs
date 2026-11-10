using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Durability.Tests.Support;

/// <summary>A command whose handler throws on its first <paramref name="Failures"/> deliveries.</summary>
public record FlakyInboxCommand(int Failures) : ICommand;

/// <summary>Counts each delivery and fails while failures remain.</summary>
public class FlakyInboxCommandHandler(DeliveryCounter deliveries) : ICommandHandler<FlakyInboxCommand>
{
    public Task HandleAsync(FlakyInboxCommand command, CancellationToken cancellationToken)
    {
        var delivery = deliveries.Next();
        if (delivery <= command.Failures)
        {
            throw new InvalidOperationException($"{nameof(FlakyInboxCommand)} failed on delivery {delivery}");
        }

        return Task.CompletedTask;
    }
}
