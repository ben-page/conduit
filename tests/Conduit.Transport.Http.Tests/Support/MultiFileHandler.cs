using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// Stub handler — never invoked because MapConduitPost throws at startup
/// validation time when the command declares two file properties.
/// </summary>
public class MultiFileHandler : ICommandHandler<MultiFileCommand, int>
{
    public Task<int> HandleAsync(MultiFileCommand command, CancellationToken cancellationToken)
        => throw new NotSupportedException("MultiFileCommand validation should have thrown before dispatch.");
}
