using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.Support;

public class GetItemFacetsHandler : IQueryHandler<GetItemFacetsQuery, ItemFacetsDto>
{
    public Task<ItemFacetsDto> HandleAsync(GetItemFacetsQuery query, CancellationToken cancellationToken)
        => Task.FromResult(new ItemFacetsDto(ItemStatus.Active, ItemReach.Self));
}
