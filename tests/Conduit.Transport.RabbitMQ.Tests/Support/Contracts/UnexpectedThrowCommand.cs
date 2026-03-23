using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

public record UnexpectedThrowCommand(string Input) : ICommand<TestCommandResult>;
