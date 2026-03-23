using Conduit.Messages;

namespace Conduit.Transport.Kafka.Tests.Support.Contracts;

public record ContextCaptureCommand(string Input) : ICommand<TestCommandResult>;
