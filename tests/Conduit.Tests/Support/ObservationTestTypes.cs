using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Tests.Support;

public sealed record SimpleVoidCommand(string Value) : ICommand;

public sealed class SimpleVoidCommandHandler : ICommandHandler<SimpleVoidCommand>
{
    public Task HandleAsync(SimpleVoidCommand command, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed record FailingCommand(string Reason) : ICommand;

public sealed class FailingCommandHandler : ICommandHandler<FailingCommand>
{
    public Task HandleAsync(FailingCommand command, CancellationToken cancellationToken)
        => throw new InvalidOperationException(command.Reason);
}

public sealed record SimpleQuery(string Value) : IQuery<string>;

public sealed class SimpleQueryHandler : IQueryHandler<SimpleQuery, string>
{
    public Task<string> HandleAsync(SimpleQuery query, CancellationToken cancellationToken)
        => Task.FromResult(query.Value);
}

public sealed record SimpleEvent(string Data) : IEvent;

public sealed class SimpleEventListener : IEventListener<SimpleEvent>
{
    public Task HandleAsync(SimpleEvent @event, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
