using Conduit.Messages;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

public record NestingCommand(string Input) : ICommand<TestCommandResult>;
