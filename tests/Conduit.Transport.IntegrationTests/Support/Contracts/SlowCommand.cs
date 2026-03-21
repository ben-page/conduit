using Conduit.Messages;

namespace Conduit.Transport.IntegrationTests.Support.Contracts;

public record SlowCommand(string Input, int DelayMs) : ICommand<TestCommandResult>;
