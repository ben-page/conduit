using System.Reflection;
using Conduit.Jobs.Persistence;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// An <see cref="IJobStore"/> whose database cannot be reached, for a heartbeat to renew against: every
/// <see cref="IJobStore.HeartbeatAsync"/> fails at once or, when built to hang, only once the call's own
/// token is cancelled, as a connection that stopped answering does. Counts the renewals it was asked for.
/// </summary>
public class UnreachableJobStore : DispatchProxy
{
    private int calls;
    private bool hangs;

    /// <summary>How many renewals have been attempted.</summary>
    public int Calls => Volatile.Read(ref this.calls);

    /// <summary>This store, as the interface the heartbeat resolves.</summary>
    public IJobStore Store => (IJobStore)(object)this;

    /// <summary>Builds a store whose renewals fail.</summary>
    /// <param name="hangs">Fail each renewal only once its token is cancelled, rather than at once.</param>
    /// <returns>The store.</returns>
    public static UnreachableJobStore Create(bool hangs = false)
    {
        var store = (UnreachableJobStore)(object)Create<IJobStore, UnreachableJobStore>();
        store.hangs = hangs;
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

        Interlocked.Increment(ref this.calls);
        if (!this.hangs)
        {
            throw new InvalidOperationException("the database cannot be reached, on purpose");
        }

        return HangAsync(args.OfType<CancellationToken>().Last());
    }

    private static async Task<HeartbeatResult> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("An infinite wait ended without being cancelled.");
    }
}
