using Conduit.Handlers;
using Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.RabbitMQ.Tests.Support.Handlers;

public class TestVoidCommandHandler(SpyLog spyLog) : ICommandHandler<TestVoidCommand>
{
    public Task HandleAsync(TestVoidCommand command, CancellationToken cancellationToken)
    {
        spyLog.Record(command);
        return Task.CompletedTask;
    }
}
