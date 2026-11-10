using Conduit.Messages;

namespace Conduit.Transport.SignalR.Tests.Support;

public record GetItemQuery(Guid Id) : IQuery<string>;
