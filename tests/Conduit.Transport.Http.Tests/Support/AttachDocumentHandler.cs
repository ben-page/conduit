using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.Support;

public class AttachDocumentHandler : ICommandHandler<AttachDocumentCommand, string>
{
    public Task<string> HandleAsync(AttachDocumentCommand command, CancellationToken cancellationToken)
        => Task.FromResult(
            $"{command.Title}|{command.Status}|{command.Document.FileName}|{command.Document.Content.ToArray().Length}");
}
