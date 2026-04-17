using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public class GenerateThumbnailHandler : ICommandHandler<GenerateThumbnailCommand, BinaryData>
{
    public Task<BinaryData> HandleAsync(GenerateThumbnailCommand command, CancellationToken cancellationToken)
    {
        return Task.FromResult(BinaryData.FromBytes(GetPngImageHandler.PngBytes, "image/png"));
    }
}
