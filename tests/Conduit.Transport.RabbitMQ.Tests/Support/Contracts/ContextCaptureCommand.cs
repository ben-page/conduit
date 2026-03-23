using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

public record ContextCaptureCommand(string Input) : ICommand<TestCommandResult>;
