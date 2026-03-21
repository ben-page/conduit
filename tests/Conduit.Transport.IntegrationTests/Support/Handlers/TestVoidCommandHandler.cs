using Conduit.Handlers;
using Conduit.Transport.IntegrationTests.Support.Contracts;

namespace Conduit.Transport.IntegrationTests.Support.Handlers;

public class TestVoidCommandHandler(SpyLog spyLog) : ICommandHandler<TestVoidCommand>
{
    public Task HandleAsync(TestVoidCommand command, CancellationToken cancellationToken)
    {
        spyLog.Record(command);
        return Task.CompletedTask;
    }
}
