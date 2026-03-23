using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

[LocalOnly]
public record LocalOnlyTestEvent(string Value) : IEvent;
