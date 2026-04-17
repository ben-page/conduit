using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public class UploadBinaryHandler(UploadedFileStore store) : ICommandHandler<UploadBinaryCommand, int>
{
    public Task<int> HandleAsync(UploadBinaryCommand command, CancellationToken cancellationToken)
    {
        store.ScreenName = command.ScreenName;
        store.Image = command.Image;
        return Task.FromResult(command.Image.ToArray().Length);
    }
}
