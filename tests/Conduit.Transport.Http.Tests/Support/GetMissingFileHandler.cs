using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

public class GetMissingFileHandler : IQueryHandler<GetMissingFileQuery, BinaryData?>
{
    public Task<BinaryData?> HandleAsync(GetMissingFileQuery query, CancellationToken cancellationToken)
    {
        return Task.FromResult<BinaryData?>(null);
    }
}
