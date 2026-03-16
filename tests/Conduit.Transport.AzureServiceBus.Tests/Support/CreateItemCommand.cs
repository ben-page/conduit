using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support;

public record CreateItemCommand(string Name) : ICommand<Guid>;
