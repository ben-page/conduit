using Conduit.Exceptions;
using Conduit.Handlers;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public class GetItemHandler(ItemStore store) : IQueryHandler<GetItemQuery, string>
{
    public Task<string> HandleAsync(GetItemQuery query, CancellationToken cancellationToken)
    {
        var name = store.Get(query.Id) ?? throw new NotFoundException($"Item {query.Id} not found");
        return Task.FromResult(name);
    }
}
