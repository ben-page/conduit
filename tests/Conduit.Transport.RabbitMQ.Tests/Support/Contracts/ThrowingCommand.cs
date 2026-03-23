using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

public record ThrowingCommand(string Input) : ICommand<TestCommandResult>;
