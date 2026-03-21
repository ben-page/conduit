using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.IntegrationTests.Support.Contracts;

[LocalOnly]
public record LocalOnlyTestEvent(string Value) : IEvent;
