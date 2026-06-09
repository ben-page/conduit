using Conduit.Handlers;
using Conduit.Runtime;

namespace Conduit.Transport.SignalR.Tests.Support;

public class CapturePropertiesHandler(CapturedProperties sink)
    : ICommandHandler<CapturePropertiesCommand, Guid>
{
    public Task<Guid> HandleAsync(CapturePropertiesCommand command, CancellationToken cancellationToken)
    {
        sink.Properties = MessageContext.Current?.Properties;
        return Task.FromResult(Guid.NewGuid());
    }
}
