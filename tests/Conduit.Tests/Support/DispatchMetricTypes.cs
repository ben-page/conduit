using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Tests.Support;

// One type per test: the meter is static and shared by parallel tests, so each test reads only its own type's series.

public sealed record MeteredCommand(string Value) : ICommand<string>;

public sealed class MeteredCommandHandler : ICommandHandler<MeteredCommand, string>
{
    public Task<string> HandleAsync(MeteredCommand command, CancellationToken cancellationToken)
        => Task.FromResult(command.Value);
}

public sealed record MeteredFailingCommand : ICommand;

public sealed class MeteredFailingCommandHandler : ICommandHandler<MeteredFailingCommand>
{
    public Task HandleAsync(MeteredFailingCommand command, CancellationToken cancellationToken)
        => throw new InvalidOperationException("metered failure");
}

public sealed record MeteredWireQuery(int Id) : IQuery<int>;

public sealed class MeteredWireQueryHandler : IQueryHandler<MeteredWireQuery, int>
{
    public Task<int> HandleAsync(MeteredWireQuery query, CancellationToken cancellationToken)
        => Task.FromResult(query.Id);
}

public sealed record MeteredEvent : IEvent;

public sealed class MeteredEventListener : IEventListener<MeteredEvent>
{
    public Task HandleAsync(MeteredEvent @event, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record MeteredWireEvent : IEvent;

public sealed class MeteredWireEventListener : IEventListener<MeteredWireEvent>
{
    public Task HandleAsync(MeteredWireEvent @event, CancellationToken cancellationToken) => Task.CompletedTask;
}
