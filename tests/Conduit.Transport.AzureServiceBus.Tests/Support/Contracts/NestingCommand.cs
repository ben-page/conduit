using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

public record NestingCommand(string Input) : ICommand<TestCommandResult>;
