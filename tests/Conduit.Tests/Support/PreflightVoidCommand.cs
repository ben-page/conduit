using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Tests.Support;

/// <summary>A void command the preflight tests judge: its pipeline is closed over <c>object</c>, as the wire's is.</summary>
public record PreflightVoidCommand : ICommand;

/// <summary>Throws if called: a preflight never runs the handler.</summary>
public class PreflightVoidCommandHandler : ICommandHandler<PreflightVoidCommand>
{
    public Task HandleAsync(PreflightVoidCommand command, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"{nameof(PreflightVoidCommand)}'s handler ran.");
}
