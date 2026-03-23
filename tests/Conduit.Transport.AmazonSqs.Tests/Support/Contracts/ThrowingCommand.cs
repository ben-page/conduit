using Conduit.Messages;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

public record ThrowingCommand(string Input) : ICommand<TestCommandResult>;
