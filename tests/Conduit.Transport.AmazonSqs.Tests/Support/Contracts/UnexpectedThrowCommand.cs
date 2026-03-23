using Conduit.Messages;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

public record UnexpectedThrowCommand(string Input) : ICommand<TestCommandResult>;
