using Conduit.Messages;

namespace Conduit.Transport.Kafka.Tests.Support.Contracts;

/// <summary>
/// A command whose handler holds execution for <paramref name="HoldMs"/> milliseconds while
/// recording concurrent execution via the <c>ConcurrencyProbe</c>. Used to observe real
/// parallelism (overlapping handler execution) within a consumer group.
/// </summary>
public record ParallelProbeCommand(string Input, int HoldMs) : ICommand<TestCommandResult>;
