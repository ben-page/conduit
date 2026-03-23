using Conduit.Messages;

namespace Conduit.Transport.Kafka.Tests.Support.Contracts;

public record TestCommand(string Input) : ICommand<TestCommandResult>;
