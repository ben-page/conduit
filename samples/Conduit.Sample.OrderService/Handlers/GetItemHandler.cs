using Conduit.Exceptions;
using Conduit.Handlers;
using Conduit.Sample.OrderService.Queries;

namespace Conduit.Sample.OrderService.Handlers;

internal sealed class GetItemHandler : IQueryHandler<GetItemQuery, ItemDto>
{
    public Task<ItemDto> HandleAsync(GetItemQuery query, CancellationToken cancellationToken)
    {
        var name = CreateItemHandler.GetItem(query.Id)
            ?? throw new NotFoundException($"Item {query.Id} not found");

        return Task.FromResult(new ItemDto(query.Id, name));
    }
}
