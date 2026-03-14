using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.OrderService.Queries;

[ClientVisible]
public record GetItemQuery(Guid Id) : IQuery<ItemDto>;

public record ItemDto(Guid Id, string Name);
