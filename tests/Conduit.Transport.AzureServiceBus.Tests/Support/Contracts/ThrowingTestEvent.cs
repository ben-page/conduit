using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

public record ThrowingTestEvent(string Value) : IEvent;
