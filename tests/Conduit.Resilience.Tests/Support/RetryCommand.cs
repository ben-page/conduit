using Conduit.Messages;

namespace Conduit.Resilience.Tests.Support;

/// <summary>
/// Command used in resilience integration tests.
/// The handler for this command fails transiently based on <see cref="CallTracker"/> state.
/// </summary>
/// <param name="FailCount">Number of times to throw <see cref="TimeoutException"/> before succeeding.</param>
public record RetryCommand(int FailCount) : ICommand<string>;
