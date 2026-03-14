using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Tests.Support;

[Dispatchable]
public record GetItemQuery(Guid Id) : IQuery<string>;
