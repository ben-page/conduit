using Conduit.Handlers;

namespace Conduit.Transport.SignalR.Tests.Support;

public class GetItemHandler(ItemStore store) : IQueryHandler<GetItemQuery, string>
{
    public Task<string> HandleAsync(GetItemQuery query, CancellationToken cancellationToken)
    {
        var name = store.Get(query.Id) ?? throw new Exceptions.NotFoundException($"Item {query.Id} not found");
        return Task.FromResult(name);
    }
}
