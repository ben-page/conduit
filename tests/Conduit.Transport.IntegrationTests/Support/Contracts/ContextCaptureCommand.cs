using Conduit.Messages;

namespace Conduit.Transport.IntegrationTests.Support.Contracts;

public record ContextCaptureCommand(string Input) : ICommand<TestCommandResult>;
