using Conduit.Handlers;
using Conduit.Runtime;

namespace Conduit.Audit.Tests.Support;

public sealed class AuditTestCommandHandler(MessageIdCapture capture) : ICommandHandler<AuditTestCommand, string>
{
    public Task<string> HandleAsync(AuditTestCommand command, CancellationToken cancellationToken)
    {
        capture.MessageId = MessageContext.Current?.MessageId;
        return Task.FromResult(command.Value);
    }
}
