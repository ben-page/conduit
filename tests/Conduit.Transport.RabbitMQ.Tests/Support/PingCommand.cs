using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public record PingCommand(string Message) : ICommand<string>;
