using Conduit.Handlers;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Handlers;

public class FireAndForgetCommandHandler(SpyLog spyLog) : ICommandHandler<FireAndForgetCommand>
{
    public Task HandleAsync(FireAndForgetCommand command, CancellationToken cancellationToken)
    {
        spyLog.Record(command);
        return Task.CompletedTask;
    }
}
