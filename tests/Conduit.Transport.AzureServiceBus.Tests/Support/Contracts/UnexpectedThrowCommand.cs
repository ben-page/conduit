using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

public record UnexpectedThrowCommand(string Input) : ICommand<TestCommandResult>;
