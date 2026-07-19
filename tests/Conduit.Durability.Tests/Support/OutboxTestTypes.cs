using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Durability.Tests.Support;

public record OrderPlacedEvent(Guid OrderId, string Product) : IEvent;

public record PlaceOrderCommand(string Product) : ICommand<Guid>;

public class PlaceOrderHandler(IMediator mediator) : ICommandHandler<PlaceOrderCommand, Guid>
{
    public async Task<Guid> HandleAsync(PlaceOrderCommand command, CancellationToken cancellationToken)
    {
        var orderId = Guid.NewGuid();
        await mediator.PublishAsync(new OrderPlacedEvent(orderId, command.Product), cancellationToken);
        return orderId;
    }
}

/// <summary>Counts local listener executions across scopes (registered singleton per test host).</summary>
public sealed class ListenerProbe
{
    private int count;

    public int Count => Volatile.Read(ref this.count);

    public void Record() => Interlocked.Increment(ref this.count);
}

[FireAndForget]
public record AuditTrailEvent(string Detail) : IEvent;

[FireAndForget(Immediate = true)]
public record LockoutStyleEvent(string Detail) : IEvent;

public class AuditTrailListener(ListenerProbe probe) : IEventListener<AuditTrailEvent>
{
    public Task HandleAsync(AuditTrailEvent @event, CancellationToken cancellationToken)
    {
        probe.Record();
        return Task.CompletedTask;
    }
}

public class LockoutStyleListener(ListenerProbe probe) : IEventListener<LockoutStyleEvent>
{
    public Task HandleAsync(LockoutStyleEvent @event, CancellationToken cancellationToken)
    {
        probe.Record();
        return Task.CompletedTask;
    }
}

public record RecordAuditCommand(string Detail) : ICommand<Guid>;

public class RecordAuditHandler(IMediator mediator) : ICommandHandler<RecordAuditCommand, Guid>
{
    public async Task<Guid> HandleAsync(RecordAuditCommand command, CancellationToken cancellationToken)
    {
        await mediator.PublishAsync(new AuditTrailEvent(command.Detail), cancellationToken);
        return Guid.NewGuid();
    }
}
