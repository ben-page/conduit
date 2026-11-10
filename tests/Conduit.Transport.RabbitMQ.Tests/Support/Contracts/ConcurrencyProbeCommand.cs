using Conduit.Messages;

namespace Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

/// <summary>
/// A command whose handler marks its entry and exit around a <paramref name="DelayMs"/> wait, so a test
/// can count how many of its handlers ran at once.
/// </summary>
public record ConcurrencyProbeCommand(string Input, int DelayMs) : ICommand<TestCommandResult>;
