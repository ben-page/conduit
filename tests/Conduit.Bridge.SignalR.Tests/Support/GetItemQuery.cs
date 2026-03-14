using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Bridge.SignalR.Tests.Support;

[Dispatchable]
public record GetItemQuery(Guid Id) : IQuery<ItemDto>;
