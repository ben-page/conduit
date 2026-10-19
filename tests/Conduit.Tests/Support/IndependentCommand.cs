using Conduit.Handlers;
using Conduit.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Support;

/// <summary>
/// A command whose handler sends an <see cref="InnerCommand"/> through a mediator from a fresh scope it creates: an
/// independent dispatch, even though it runs inside the outer one's attempt.
/// </summary>
public record IndependentCommand(int InnerFailures) : ICommand;

public class IndependentCommandHandler(IServiceScopeFactory scopeFactory, RetryProbe probe, AttemptProbe scope)
    : ICommandHandler<IndependentCommand>
{
    /// <summary>The site the handler records the fresh scope's probe at.</summary>
    public const string FreshScopeSite = nameof(IndependentCommand) + ".freshScope";

    public async Task HandleAsync(IndependentCommand command, CancellationToken cancellationToken)
    {
        probe.See(nameof(IndependentCommand), scope);
        await using var freshScope = scopeFactory.CreateAsyncScope();
        probe.See(FreshScopeSite, freshScope.ServiceProvider.GetRequiredService<AttemptProbe>());
        var mediator = freshScope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.ExecuteAsync(new InnerCommand(command.InnerFailures), cancellationToken);
    }
}
