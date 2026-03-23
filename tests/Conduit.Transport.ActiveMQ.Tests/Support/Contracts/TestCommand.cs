using Conduit.Messages;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

public record TestCommand(string Input) : ICommand<TestCommandResult>;
