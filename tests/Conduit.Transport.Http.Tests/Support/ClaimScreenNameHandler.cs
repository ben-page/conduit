using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.Support;

public class ClaimScreenNameHandler : ICommandHandler<ClaimScreenNameCommand, string>
{
    public Task<string> HandleAsync(ClaimScreenNameCommand command, CancellationToken cancellationToken)
        => Task.FromResult($"{command.ScreenName}|{command.Role}");
}
