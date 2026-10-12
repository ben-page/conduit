using Conduit.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// A <see cref="PeriodicCleanupServiceBase"/> whose pass waits until the host stops and then throws an
/// exception that is not cancellation — what SqlClient does with a command a stopping host cancels.
/// </summary>
public sealed class StopCancelledCleanup : PeriodicCleanupServiceBase
{
    private readonly TaskCompletionSource passStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Initializes a new instance of <see cref="StopCancelledCleanup"/>.</summary>
    /// <param name="scopeFactory">Factory used to scope each pass.</param>
    public StopCancelledCleanup(IServiceScopeFactory scopeFactory)
        : base(scopeFactory)
    {
    }

    /// <summary>Completes when the pass is under way, waiting for the stop.</summary>
    public Task PassStarted => this.passStarted.Task;

    /// <inheritdoc/>
    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    /// <inheritdoc/>
    protected override async Task CleanupAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        this.passStarted.TrySetResult();

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancelled: now fail as SqlClient does, with something that is not cancellation.
        }

        throw new InvalidOperationException("Operation cancelled by user, as SqlClient reports it, on purpose");
    }
}
