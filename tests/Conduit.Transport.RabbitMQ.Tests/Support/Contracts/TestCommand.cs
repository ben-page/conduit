using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

public record TestCommand(string Input) : ICommand<TestCommandResult>;
