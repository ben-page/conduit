using Conduit.Handlers;
using Conduit.Messages;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Tests.Support;

/// <summary>
/// The owner: its handler publishes an <see cref="AuditRetryNotedEvent"/> and sends an
/// <see cref="AuditRetryInnerCommand"/> through its injected mediator (both nested), then throws
/// <see cref="RetryMeException"/> on its first <paramref name="Failures"/> calls.
/// </summary>
public sealed record AuditRetryOwnerCommand(int Failures) : ICommand;

public sealed class AuditRetryOwnerCommandHandler(IMediator mediator, AuditRetryCalls calls)
    : ICommandHandler<AuditRetryOwnerCommand>
{
    public async Task HandleAsync(AuditRetryOwnerCommand command, CancellationToken cancellationToken)
    {
        await mediator.PublishAsync(new AuditRetryNotedEvent("noted"), cancellationToken);
        await mediator.ExecuteAsync(new AuditRetryInnerCommand("inner"), cancellationToken);
        if (calls.Count(nameof(AuditRetryOwnerCommand)) <= command.Failures)
        {
            throw new RetryMeException("owner failed after its nested dispatches");
        }
    }
}

/// <summary>An audited event the owner publishes.</summary>
public sealed record AuditRetryNotedEvent(string Note) : IEvent;

public sealed class AuditRetryNotedEventListener : IEventListener<AuditRetryNotedEvent>
{
    public Task HandleAsync(AuditRetryNotedEvent @event, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>An audited command the owner sends.</summary>
public sealed record AuditRetryInnerCommand(string Note) : ICommand;

public sealed class AuditRetryInnerCommandHandler : ICommandHandler<AuditRetryInnerCommand>
{
    public Task HandleAsync(AuditRetryInnerCommand command, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Sends an <see cref="AuditRetryInnerCommand"/> from a fresh scope it creates (an independent dispatch), then throws
/// <see cref="RetryMeException"/> on its first <paramref name="Failures"/> calls.
/// </summary>
public sealed record AuditRetryIndependentCommand(int Failures) : ICommand;

public sealed class AuditRetryIndependentCommandHandler(IServiceScopeFactory scopeFactory, AuditRetryCalls calls)
    : ICommandHandler<AuditRetryIndependentCommand>
{
    public async Task HandleAsync(AuditRetryIndependentCommand command, CancellationToken cancellationToken)
    {
        await using (var freshScope = scopeFactory.CreateAsyncScope())
        {
            var mediator = freshScope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.ExecuteAsync(new AuditRetryInnerCommand("independent"), cancellationToken);
        }

        if (calls.Count(nameof(AuditRetryIndependentCommand)) <= command.Failures)
        {
            throw new RetryMeException("independent owner failed after its dispatch");
        }
    }
}
