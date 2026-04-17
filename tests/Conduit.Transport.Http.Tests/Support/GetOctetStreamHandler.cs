using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public class GetOctetStreamHandler : IQueryHandler<GetOctetStreamQuery, BinaryData?>
{
    public static readonly byte[] Bytes = [0x01, 0x02, 0x03, 0x04, 0x05];

    public Task<BinaryData?> HandleAsync(GetOctetStreamQuery query, CancellationToken cancellationToken)
    {
        return Task.FromResult<BinaryData?>(BinaryData.FromBytes(Bytes));
    }
}
