using Conduit.Messages;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

public record SlowCommand(string Input, int DelayMs) : ICommand<TestCommandResult>;
