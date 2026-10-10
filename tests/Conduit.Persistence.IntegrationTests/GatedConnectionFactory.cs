using System.Data.Common;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// Wraps an engine's connection factory so a test can stop one store call between its statements.
/// The pause points are command-text prefixes, taken in order: the first command matching point 0 runs
/// and then waits at a gate until the test releases point 0; after that the first command matching
/// point 1 does the same, and so on. Every other command passes straight through. That is what lets a
/// test hold a call at an exact place inside its transaction and interleave another call there, which
/// no amount of timing can do reliably.
/// </summary>
internal sealed class GatedConnectionFactory : IConduitConnectionFactory
{
    private readonly IConduitConnectionFactory inner;
    private readonly string[] pausePoints;
    private readonly TaskCompletionSource[] paused;
    private readonly TaskCompletionSource[] released;
    private int next;

    /// <summary>Wraps <paramref name="inner"/> with the given pause points.</summary>
    /// <param name="inner">The engine's factory.</param>
    /// <param name="pauseAfterCommandsStartingWith">The command-text prefix of each pause point, in order.</param>
    public GatedConnectionFactory(IConduitConnectionFactory inner, params string[] pauseAfterCommandsStartingWith)
    {
        this.inner = inner;
        this.pausePoints = pauseAfterCommandsStartingWith;
        this.paused = [.. pauseAfterCommandsStartingWith.Select(_ => NewSignal())];
        this.released = [.. pauseAfterCommandsStartingWith.Select(_ => NewSignal())];
    }

    /// <summary>Completes once the command for <paramref name="point"/> has run and is waiting.</summary>
    public Task PausedAt(int point) => this.paused[point].Task;

    /// <summary>Lets the call held at <paramref name="point"/> carry on.</summary>
    public void Release(int point) => this.released[point].TrySetResult();

    /// <inheritdoc/>
    public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
        => new GatedConnection(await this.inner.CreateConnectionAsync(cancellationToken), this);

    /// <summary>Called after every command; pauses after the one the next pause point names.</summary>
    internal async Task AfterCommandAsync(string commandText)
    {
        var point = Volatile.Read(ref this.next);
        if (point < this.pausePoints.Length
            && commandText.StartsWith(this.pausePoints[point], StringComparison.Ordinal)
            && Interlocked.CompareExchange(ref this.next, point + 1, point) == point)
        {
            this.paused[point].TrySetResult();
            await this.released[point].Task;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
