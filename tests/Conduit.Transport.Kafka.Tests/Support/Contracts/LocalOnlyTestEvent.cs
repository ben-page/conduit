using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.Kafka.Tests.Support.Contracts;

[LocalOnly]
public record LocalOnlyTestEvent(string Value) : IEvent;
