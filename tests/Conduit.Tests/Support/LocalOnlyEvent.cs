using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Tests.Support;

[LocalOnly]
public record LocalOnlyEvent(string Data) : IEvent;
