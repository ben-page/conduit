using Conduit.Attributes;
using Conduit.Durability.Runtime.Outbox;
using Conduit.Handlers;
using Conduit.Messages;
using Conduit.Middleware;
using Conduit.Runtime;

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

/// <summary>Counts local listener executions across scopes (registered singleton per test host). Optionally
/// throws on its first call, and records the <c>MessageId</c> and properties each call ran under.</summary>
public sealed class ListenerProbe
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string?> messageIds = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<IReadOnlyDictionary<string, object?>> properties = new();
    private int count;
    private int throwsRemaining;

    public int Count => Volatile.Read(ref this.count);

    /// <summary>The <c>MessageContext.Current.MessageId</c> of each listener call, in call order.</summary>
    public IReadOnlyList<string?> MessageIds => [.. this.messageIds];

    /// <summary>The <c>MessageContext.Current.Properties</c> of each listener call, in call order.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Properties => [.. this.properties];

    /// <summary>The <c>MessageId</c> the listener's nested <see cref="ProbeNestedCommand"/> ran under.</summary>
    public string? NestedMessageId { get; set; }

    /// <summary>When set, the listener executes a <see cref="ProbeNestedCommand"/> on each call.</summary>
    public bool ExecuteNested { get; set; }

    /// <summary>Makes the next <paramref name="times"/> calls throw (after being recorded).</summary>
    public void ThrowOnNextCalls(int times) => Volatile.Write(ref this.throwsRemaining, times);

    public void Record()
    {
        Interlocked.Increment(ref this.count);
        this.messageIds.Enqueue(MessageContext.Current?.MessageId);
        this.properties.Enqueue(MessageContext.Current?.Properties ?? new Dictionary<string, object?>());

        if (Interlocked.Decrement(ref this.throwsRemaining) >= 0)
        {
            throw new InvalidOperationException("probe failure");
        }

        Interlocked.Exchange(ref this.throwsRemaining, 0);
    }
}

[FireAndForget]
public record AuditTrailEvent(string Detail) : IEvent;

[FireAndForget(Immediate = true)]
public record LockoutStyleEvent(string Detail) : IEvent;

public class AuditTrailListener(ListenerProbe probe, IMediator mediator) : IEventListener<AuditTrailEvent>
{
    public async Task HandleAsync(AuditTrailEvent @event, CancellationToken cancellationToken)
    {
        probe.Record();

        if (probe.ExecuteNested)
        {
            await mediator.ExecuteAsync(new ProbeNestedCommand(), cancellationToken);
        }
    }
}

/// <summary>A command the audit-trail listener executes, to observe whose delivery it is caused by.</summary>
public record ProbeNestedCommand : ICommand<Guid>;

public class ProbeNestedHandler(ListenerProbe probe) : ICommandHandler<ProbeNestedCommand, Guid>
{
    public Task<Guid> HandleAsync(ProbeNestedCommand command, CancellationToken cancellationToken)
    {
        probe.NestedMessageId = MessageContext.Current?.MessageId;
        return Task.FromResult(Guid.NewGuid());
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

/// <summary>The command a Conduit.Jobs event trigger on <see cref="AuditTrailEvent"/> runs.</summary>
public sealed record TriggeredAuditJob(string Detail) : ICommand;

/// <summary>Counts the triggered job's runs (registered singleton per test host).</summary>
public sealed class TriggeredJobSpy
{
    private int runs;

    public int Runs => Volatile.Read(ref this.runs);

    public void Record() => Interlocked.Increment(ref this.runs);
}

public class TriggeredAuditJobHandler(TriggeredJobSpy spy) : ICommandHandler<TriggeredAuditJob>
{
    public Task HandleAsync(TriggeredAuditJob command, CancellationToken cancellationToken)
    {
        spy.Record();
        return Task.CompletedTask;
    }
}
