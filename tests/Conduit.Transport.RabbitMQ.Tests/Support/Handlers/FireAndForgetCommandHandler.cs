using Conduit.Handlers;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Handlers;

public class FireAndForgetCommandHandler(SpyLog spyLog) : ICommandHandler<FireAndForgetCommand>
{
    public Task HandleAsync(FireAndForgetCommand command, CancellationToken cancellationToken)
    {
        spyLog.Record(command);
        return Task.CompletedTask;
    }
}
