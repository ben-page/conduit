using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.Support;

public class ClaimScreenNamePositionallyHandler : ICommandHandler<ClaimScreenNamePositionallyCommand, string>
{
    public Task<string> HandleAsync(ClaimScreenNamePositionallyCommand command, CancellationToken cancellationToken)
        => Task.FromResult($"{command.ScreenName}|{command.Role}");
}
