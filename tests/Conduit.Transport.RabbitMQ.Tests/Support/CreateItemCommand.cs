using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public record CreateItemCommand(string Name) : ICommand<Guid>;
