using Conduit.Messages;

namespace Conduit.Transport.Kafka.Tests.Support.Contracts;

public record ThrowingCommand(string Input) : ICommand<TestCommandResult>;
