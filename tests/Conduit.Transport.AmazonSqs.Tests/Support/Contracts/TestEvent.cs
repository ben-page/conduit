using Conduit.Messages;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

public record TestEvent(string Value) : IEvent;
