using Conduit.Handlers;
using Conduit.TestUtils;
using Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Handlers;

public class FireAndForgetCommandHandler(SpyLog spyLog) : ICommandHandler<FireAndForgetCommand>
{
    public Task HandleAsync(FireAndForgetCommand command, CancellationToken cancellationToken)
    {
        spyLog.Record(command);
        return Task.CompletedTask;
    }
}
