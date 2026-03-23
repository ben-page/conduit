using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

public record ThrowingCommand(string Input) : ICommand<TestCommandResult>;
