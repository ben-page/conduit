using Conduit.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// A <see cref="PeriodicCleanupServiceBase"/> whose first pass throws, as a purge chosen as a deadlock
/// victim does, on a short interval so the pass after it comes round within a test.
/// </summary>
public sealed class FirstPassFailingCleanup : PeriodicCleanupServiceBase
{
    private readonly TaskCompletionSource secondPass = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int passes;

    /// <summary>Initializes a new instance of <see cref="FirstPassFailingCleanup"/>.</summary>
    /// <param name="scopeFactory">Factory used to scope each pass.</param>
    public FirstPassFailingCleanup(IServiceScopeFactory scopeFactory)
        : base(scopeFactory)
    {
    }

    /// <summary>Completes when the pass after the failing one has run.</summary>
    public Task SecondPass => this.secondPass.Task;

    /// <inheritdoc/>
    protected override TimeSpan Interval => TimeSpan.FromMilliseconds(20);

    /// <inheritdoc/>
    protected override async Task CleanupAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        // Off the host's start, as a real pass's database round trip is: a pass failing inside
        // StartAsync would fail the start instead.
        await Task.Yield();

        if (Interlocked.Increment(ref this.passes) == 1)
        {
            throw new InvalidOperationException("the database chose this purge as a deadlock victim, on purpose");
        }

        this.secondPass.TrySetResult();
    }
}
