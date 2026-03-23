using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

public record NestingCommand(string Input) : ICommand<TestCommandResult>;
