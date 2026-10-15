using Conduit.Attributes;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Handlers;
using Conduit.Messages;
using Conduit.Middleware;

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

/// <summary>A plain (inline) event: its listener's pipeline runs inside the publishing handler's.</summary>
public record AuditStepEvent(int Step) : IEvent;

public class AuditStepListener : IEventListener<AuditStepEvent>
{
    public Task HandleAsync(AuditStepEvent @event, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Captures one fire-and-forget event, then publishes two plain events inline — each of
/// which runs a nested pipeline (and so a nested outbox behavior) in the same dispatch scope.</summary>
public record RecordAuditWithStepsCommand(string Detail) : ICommand<Guid>;

public class RecordAuditWithStepsHandler(IMediator mediator) : ICommandHandler<RecordAuditWithStepsCommand, Guid>
{
    public async Task<Guid> HandleAsync(RecordAuditWithStepsCommand command, CancellationToken cancellationToken)
    {
        await mediator.PublishAsync(new AuditTrailEvent(command.Detail), cancellationToken);
        await mediator.PublishAsync(new AuditStepEvent(1), cancellationToken);
        await mediator.PublishAsync(new AuditStepEvent(2), cancellationToken);
        return Guid.NewGuid();
    }
}

/// <summary>A command whose handler publishes nothing: <see cref="PostHandlerPublishBehavior{TRequest,TResult}"/>
/// publishes its audit event after the pipeline (and so every OutboxBehavior) has exited.</summary>
public record DeferredAuditCommand(string Detail, bool WritePending) : ICommand<Guid>;

public class DeferredAuditHandler : ICommandHandler<DeferredAuditCommand, Guid>
{
    public Task<Guid> HandleAsync(DeferredAuditCommand command, CancellationToken cancellationToken)
        => Task.FromResult(Guid.NewGuid());
}

/// <summary>Models a host's outermost transactional behavior that dispatches domain events after
/// <c>next()</c> returns — outside every OutboxBehavior — and then writes what they captured.</summary>
public sealed class PostHandlerPublishBehavior<TRequest, TResult>(IMediator mediator, IOutboxPendingWriter pendingWriter)
    : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var result = await next();

        if (request is DeferredAuditCommand command)
        {
            await mediator.PublishAsync(new AuditTrailEvent(command.Detail), cancellationToken);
            if (command.WritePending)
            {
                await pendingWriter.WritePendingAsync(cancellationToken);
            }
        }

        return result;
    }
}
