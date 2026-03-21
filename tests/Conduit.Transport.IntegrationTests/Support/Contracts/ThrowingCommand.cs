using Conduit.Messages;

namespace Conduit.Transport.IntegrationTests.Support.Contracts;

public record ThrowingCommand(string Input) : ICommand<TestCommandResult>;
