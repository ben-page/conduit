using Conduit.Handlers;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Handlers;

public class ThrowingFireAndForgetCommandHandler(SpyLog spyLog) : ICommandHandler<ThrowingFireAndForgetCommand>
{
    public Task HandleAsync(ThrowingFireAndForgetCommand command, CancellationToken cancellationToken)
    {
        spyLog.Record(command);
        throw new InvalidOperationException("Fire-and-forget handler failure");
    }
}
