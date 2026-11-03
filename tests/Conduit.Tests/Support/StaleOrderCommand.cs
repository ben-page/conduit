using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Tests.Support;

/// <summary>A void command whose handler throws a <see cref="TimeoutException"/>, which <see cref="PreflightTranslator"/> claims as a refusal.</summary>
public record StaleOrderCommand : ICommand;

public class StaleOrderCommandHandler : ICommandHandler<StaleOrderCommand>
{
    public Task HandleAsync(StaleOrderCommand command, CancellationToken cancellationToken) =>
        throw new TimeoutException("stale row");
}
