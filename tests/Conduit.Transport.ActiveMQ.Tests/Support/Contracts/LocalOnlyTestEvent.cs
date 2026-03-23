using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

[LocalOnly]
public record LocalOnlyTestEvent(string Value) : IEvent;
