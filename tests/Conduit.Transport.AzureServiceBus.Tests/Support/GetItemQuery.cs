using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support;

public record GetItemQuery(Guid Id) : IQuery<string>;
