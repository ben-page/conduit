using Conduit.Messages;

namespace Conduit.Transport.IntegrationTests.Support.Contracts;

public record NestingCommand(string Input) : ICommand<TestCommandResult>;
