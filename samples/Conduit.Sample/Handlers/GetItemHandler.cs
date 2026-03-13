using Conduit;
using Conduit.Exceptions;
using Conduit.Sample.Queries;

namespace Conduit.Sample.Handlers;

public class GetItemHandler : IQueryHandler<GetItemQuery, ItemDto>
{
    public Task<ItemDto> HandleAsync(GetItemQuery query, CancellationToken cancellationToken)
    {
        var name = CreateItemHandler.GetItem(query.Id)
            ?? throw new NotFoundException($"Item {query.Id} not found");

        return Task.FromResult(new ItemDto(query.Id, name));
    }
}
