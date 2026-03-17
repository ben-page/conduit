using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public record DeleteItemCommand(Guid Id) : ICommand;
