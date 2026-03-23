using Conduit.Messages;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

public record ContextCaptureCommand(string Input) : ICommand<TestCommandResult>;
