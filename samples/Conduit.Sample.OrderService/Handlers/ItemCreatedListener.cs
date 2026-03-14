using Conduit.Handlers;
using Conduit.Sample.OrderService.Events;

namespace Conduit.Sample.OrderService.Handlers;

internal sealed partial class ItemCreatedListener(ILogger<ItemCreatedListener> logger) : IEventListener<ItemCreatedEvent>
{
    public Task HandleAsync(ItemCreatedEvent @event, CancellationToken cancellationToken)
    {
        LogItemCreated(logger, @event.Id, @event.Name);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Item created: {Id} - {Name}")]
    private static partial void LogItemCreated(ILogger logger, Guid id, string name);
}
