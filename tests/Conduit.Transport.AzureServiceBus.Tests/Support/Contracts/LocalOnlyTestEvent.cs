using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

[LocalOnly]
public record LocalOnlyTestEvent(string Value) : IEvent;
