using Conduit;
using Conduit.Attributes;

namespace Conduit.Sample.Queries;

[Dispatchable]
public record GetItemQuery(Guid Id) : IQuery<ItemDto>;

public record ItemDto(Guid Id, string Name);
