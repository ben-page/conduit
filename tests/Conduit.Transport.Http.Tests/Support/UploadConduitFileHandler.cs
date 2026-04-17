using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public class UploadConduitFileHandler(UploadedFileStore store) : ICommandHandler<UploadConduitFileCommand, int>
{
    public Task<int> HandleAsync(UploadConduitFileCommand command, CancellationToken cancellationToken)
    {
        store.ScreenName = command.ScreenName;
        store.File = command.Document;
        return Task.FromResult(command.Document.Content.ToArray().Length);
    }
}
