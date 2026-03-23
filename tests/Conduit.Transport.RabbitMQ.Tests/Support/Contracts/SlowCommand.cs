using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

public record SlowCommand(string Input, int DelayMs) : ICommand<TestCommandResult>;
