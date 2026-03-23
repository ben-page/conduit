using Conduit.Messages;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

public record SlowCommand(string Input, int DelayMs) : ICommand<TestCommandResult>;
