using Conduit.Messages;

namespace Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

public record ContextCaptureCommand(string Input) : ICommand<TestCommandResult>;
