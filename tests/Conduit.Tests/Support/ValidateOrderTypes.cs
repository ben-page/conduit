using Conduit.Exceptions;
using Conduit.Handlers;
using Conduit.Messages;
using Conduit.Middleware;

namespace Conduit.Tests.Support;

// Fixtures for ValidateOrderShould: every message records "validate" and then refuses its input, every handler
// records "handler", and the two behaviors record "behavior" (one refuses, one passes through). The record is a
// DI singleton the message carries, so one ordered list shows which of the three ran, and in what order.

public class DispatchOrderRecord
{
    private readonly List<string> entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (this.entries)
            {
                return [.. this.entries];
            }
        }
    }

    public void Add(string entry)
    {
        lock (this.entries)
        {
            this.entries.Add(entry);
        }
    }
}

public class RefusedByBehaviorException() : Exception("refused by behavior");

public class RefusingBehavior<TRequest, TResult>(DispatchOrderRecord record) : IPipelineBehavior<TRequest, TResult>
{
    public Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        record.Add("behavior");
        throw new RefusedByBehaviorException();
    }
}

public class PassingBehavior<TRequest, TResult>(DispatchOrderRecord record) : IPipelineBehavior<TRequest, TResult>
{
    public Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        record.Add("behavior");
        return next();
    }
}

// --- Query ---

public record InvalidOrderQuery(DispatchOrderRecord Record) : IQuery<string>
{
    public void Validate()
    {
        this.Record.Add("validate");
        throw new BadRequestException("invalid query");
    }
}

public class InvalidOrderQueryHandler(DispatchOrderRecord record) : IQueryHandler<InvalidOrderQuery, string>
{
    public Task<string> HandleAsync(InvalidOrderQuery query, CancellationToken cancellationToken)
    {
        record.Add("handler");
        return Task.FromResult("handled");
    }
}

// --- Command with a result ---

public record InvalidOrderResultCommand(DispatchOrderRecord Record) : ICommand<string>
{
    public void Validate()
    {
        this.Record.Add("validate");
        throw new BadRequestException("invalid command");
    }
}

public class InvalidOrderResultCommandHandler(DispatchOrderRecord record)
    : ICommandHandler<InvalidOrderResultCommand, string>
{
    public Task<string> HandleAsync(InvalidOrderResultCommand command, CancellationToken cancellationToken)
    {
        record.Add("handler");
        return Task.FromResult("handled");
    }
}

// --- Void command ---

public record InvalidOrderVoidCommand(DispatchOrderRecord Record) : ICommand
{
    public void Validate()
    {
        this.Record.Add("validate");
        throw new BadRequestException("invalid command");
    }
}

public class InvalidOrderVoidCommandHandler(DispatchOrderRecord record) : ICommandHandler<InvalidOrderVoidCommand>
{
    public Task HandleAsync(InvalidOrderVoidCommand command, CancellationToken cancellationToken)
    {
        record.Add("handler");
        return Task.CompletedTask;
    }
}

// --- Fire-and-forget void command ---

[Conduit.Attributes.FireAndForget]
public record InvalidOrderFireAndForgetCommand(DispatchOrderRecord Record) : ICommand
{
    public void Validate()
    {
        this.Record.Add("validate");
        throw new BadRequestException("invalid command");
    }
}

public class InvalidOrderFireAndForgetCommandHandler(DispatchOrderRecord record)
    : ICommandHandler<InvalidOrderFireAndForgetCommand>
{
    public Task HandleAsync(InvalidOrderFireAndForgetCommand command, CancellationToken cancellationToken)
    {
        record.Add("handler");
        return Task.CompletedTask;
    }
}
