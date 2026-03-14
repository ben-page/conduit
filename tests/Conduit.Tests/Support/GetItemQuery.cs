using Conduit.Messages;

namespace Conduit.Tests.Support;

public record GetItemQuery(Guid Id) : IQuery<string>;
