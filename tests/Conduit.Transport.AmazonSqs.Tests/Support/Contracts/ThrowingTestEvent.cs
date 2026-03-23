using Conduit.Messages;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

public record ThrowingTestEvent(string Value) : IEvent;
