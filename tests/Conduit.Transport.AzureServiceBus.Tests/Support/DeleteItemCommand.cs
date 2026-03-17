using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support;

public record DeleteItemCommand(Guid Id) : ICommand;
