using Conduit.Exceptions;
using Conduit.Handlers;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public class GetItemHandler : IQueryHandler<GetItemQuery, string>
{
    public Task<string> HandleAsync(GetItemQuery query, CancellationToken cancellationToken)
    {
        throw new NotFoundException($"Item {query.Id} not found");
    }
}
