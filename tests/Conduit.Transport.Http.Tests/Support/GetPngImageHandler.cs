using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public class GetPngImageHandler : IQueryHandler<GetPngImageQuery, BinaryData?>
{
    public static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD, 0xBE, 0xEF];

    public Task<BinaryData?> HandleAsync(GetPngImageQuery query, CancellationToken cancellationToken)
    {
        return Task.FromResult<BinaryData?>(BinaryData.FromBytes(PngBytes, "image/png"));
    }
}
