using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

/// <summary>
/// A command whose handler records itself on entry, then runs for <paramref name="DelayMs"/> ignoring
/// cancellation: a shutdown test stops the host while it is still running.
/// </summary>
public record ShutdownProbeCommand(string Input, int DelayMs) : ICommand<TestCommandResult>;
