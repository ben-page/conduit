using System.Reflection;
using Conduit.Jobs.Persistence;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// An <see cref="IJobStore"/> whose database cannot be reached, for a heartbeat to renew against: every
/// <see cref="IJobStore.HeartbeatAsync"/> fails, at once (a refused connection) or, from a chosen call on,
/// only once the call's own token is cancelled (a connection that stopped answering). Counts the
/// renewals it was asked for.
/// </summary>
public class UnreachableJobStore : DispatchProxy
{
    // A hang the caller never cancels still ends, so a test that regresses fails rather than holding the
    // run open: well past any test's fake-clock work, and within its [Timeout].
    private static readonly TimeSpan LongestHang = TimeSpan.FromSeconds(30);

    private int calls;
    private int hangFromCall;

    /// <summary>How many renewals have been attempted.</summary>
    public int Calls => Volatile.Read(ref this.calls);

    /// <summary>This store, as the interface the heartbeat resolves.</summary>
    public IJobStore Store => (IJobStore)(object)this;

    /// <summary>Builds a store whose renewals fail.</summary>
    /// <param name="hangFromCall">
    /// The first renewal, counting from 1, that hangs until its token is cancelled instead of failing at
    /// once. The default never hangs; 1 hangs every renewal.
    /// </param>
    /// <returns>The store.</returns>
    public static UnreachableJobStore Create(int hangFromCall = int.MaxValue)
    {
        var store = (UnreachableJobStore)(object)Create<IJobStore, UnreachableJobStore>();
        store.hangFromCall = hangFromCall;
        return store;
    }

    /// <summary>Polls until at least <paramref name="expected"/> renewals have been attempted, or throws.</summary>
    /// <param name="expected">The number of renewals to wait for.</param>
    /// <returns>A task that completes once they have been attempted.</returns>
    public async Task WaitForCallsAsync(int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (this.Calls < expected)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {expected} renewals but saw {this.Calls}.");
            }

            await Task.Delay(10);
        }
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        ArgumentNullException.ThrowIfNull(args);

        if (targetMethod.Name != nameof(IJobStore.HeartbeatAsync))
        {
            throw new NotSupportedException($"Only the heartbeat reaches this store, not {targetMethod.Name}.");
        }

        var call = Interlocked.Increment(ref this.calls);
        if (call < this.hangFromCall)
        {
            throw new InvalidOperationException("the database cannot be reached, on purpose");
        }

        return HangAsync(args.OfType<CancellationToken>().Last());
    }

    private static async Task<HeartbeatResult> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(LongestHang, cancellationToken);
        throw new InvalidOperationException("the database never answered, on purpose");
    }
}
