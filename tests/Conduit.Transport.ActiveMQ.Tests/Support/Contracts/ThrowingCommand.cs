using Conduit.Messages;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

public record ThrowingCommand(string Input) : ICommand<TestCommandResult>;
