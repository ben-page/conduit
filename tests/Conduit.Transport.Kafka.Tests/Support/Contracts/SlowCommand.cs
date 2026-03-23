using Conduit.Messages;

namespace Conduit.Transport.Kafka.Tests.Support.Contracts;

public record SlowCommand(string Input, int DelayMs) : ICommand<TestCommandResult>;
