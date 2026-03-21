using Conduit.Messages;

namespace Conduit.Transport.IntegrationTests.Support.Contracts;

public record UnexpectedThrowCommand(string Input) : ICommand<TestCommandResult>;
