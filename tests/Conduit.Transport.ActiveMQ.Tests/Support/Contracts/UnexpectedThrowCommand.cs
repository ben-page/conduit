using Conduit.Messages;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

public record UnexpectedThrowCommand(string Input) : ICommand<TestCommandResult>;
