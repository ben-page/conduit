using Conduit.Messages;

namespace Conduit.Transport.Kafka.Tests.Support.Contracts;

public record UnexpectedThrowCommand(string Input) : ICommand<TestCommandResult>;
