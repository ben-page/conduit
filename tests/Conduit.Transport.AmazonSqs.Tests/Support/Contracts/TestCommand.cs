using Conduit.Messages;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

public record TestCommand(string Input) : ICommand<TestCommandResult>;
