using Conduit.Exceptions;
using Conduit.Handlers;

namespace Conduit.Bridge.SignalR.Tests.Support;

public class GetItemHandler(ItemStore store) : IQueryHandler<GetItemQuery, ItemDto>
{
    public Task<ItemDto> HandleAsync(GetItemQuery query, CancellationToken cancellationToken)
    {
        var name = store.Get(query.Id)
            ?? throw new NotFoundException($"Item {query.Id} not found");

        return Task.FromResult(new ItemDto(query.Id, name));
    }
}
