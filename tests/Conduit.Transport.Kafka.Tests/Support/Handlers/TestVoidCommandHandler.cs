using Conduit.Handlers;
using Conduit.Transport.Kafka.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.Kafka.Tests.Support.Handlers;

public class TestVoidCommandHandler(SpyLog spyLog) : ICommandHandler<TestVoidCommand>
{
    public Task HandleAsync(TestVoidCommand command, CancellationToken cancellationToken)
    {
        spyLog.Record(command);
        return Task.CompletedTask;
    }
}
