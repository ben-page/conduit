using Conduit.Messages;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

public record NestingCommand(string Input) : ICommand<TestCommandResult>;
