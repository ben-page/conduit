using Conduit.Messages;

namespace Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

public record ContextCaptureCommand(string Input) : ICommand<TestCommandResult>;
