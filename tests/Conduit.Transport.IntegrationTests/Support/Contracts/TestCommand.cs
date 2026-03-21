using Conduit.Messages;

namespace Conduit.Transport.IntegrationTests.Support.Contracts;

public record TestCommand(string Input) : ICommand<TestCommandResult>;
