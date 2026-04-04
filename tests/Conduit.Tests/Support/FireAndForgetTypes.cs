using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;
using Conduit.TestUtils;

namespace Conduit.Tests.Support;

// --- Fire-and-forget command (void, no result) ---

[FireAndForget]
public record ImportDataCommand(string Path) : ICommand;

public class ImportDataHandler(EventSpy<ImportDataCommand> spy) : ICommandHandler<ImportDataCommand>
{
    public async Task HandleAsync(ImportDataCommand command, CancellationToken cancellationToken)
    {
        // Simulate slow work to prove the caller doesn't wait
        await Task.Delay(500, cancellationToken);
        spy.Record(command);
    }
}

// --- Fire-and-forget command that throws ---

[FireAndForget]
public record FailingFireAndForgetCommand(string Reason) : ICommand;

public class FailingFireAndForgetHandler(
    EventSpy<FailingFireAndForgetCommand> spy) : ICommandHandler<FailingFireAndForgetCommand>
{
    public Task HandleAsync(FailingFireAndForgetCommand command, CancellationToken cancellationToken)
    {
        spy.Record(command);
        throw new InvalidOperationException(command.Reason);
    }
}

// --- Fire-and-forget event ---

[FireAndForget]
public record AuditLogEvent(string Action) : IEvent;

public class AuditLogListener(EventSpy<AuditLogEvent> spy) : IEventListener<AuditLogEvent>
{
    public async Task HandleAsync(AuditLogEvent @event, CancellationToken cancellationToken)
    {
        await Task.Delay(500, cancellationToken);
        spy.Record(@event);
    }
}

// --- Invalid: FireAndForget on ICommand<TResult> (should emit CONDUIT002) ---
// This type is only used in source gen diagnostic tests, not in integration tests.
