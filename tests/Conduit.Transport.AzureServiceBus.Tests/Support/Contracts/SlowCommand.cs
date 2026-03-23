using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

public record SlowCommand(string Input, int DelayMs) : ICommand<TestCommandResult>;
