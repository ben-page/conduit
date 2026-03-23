using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

public record TestCommand(string Input) : ICommand<TestCommandResult>;
