using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// One outbox entry's delivery, shared by the relay (<see cref="OutboxRelay"/>) and the post-commit flush
/// (<see cref="OutboxCapture.ClaimAndDeliverAsync"/>). Each creates a fresh scope per entry, dispatches the entry inside
/// it, writes the entry's outcome, and only then disposes the scope. Marking the entry sent is the point of no return,
/// so nothing after it, the disposal included, can turn a delivered entry into a retried one.
/// </summary>
internal static class OutboxDelivery
{
    /// <summary>
    /// Delivers the entry's envelope: local listeners first, resolved from <paramref name="entryServices"/> (mirroring
    /// fire-and-forget's fresh-scope semantics), then transports. Redelivery re-runs local listeners: the
    /// idempotent-listener requirement is load-bearing.
    /// </summary>
    /// <param name="envelope">The entry's envelope.</param>
    /// <param name="entryServices">The entry's own scope, which the caller disposes after writing the outcome.</param>
    /// <param name="transportDispatcher">Sends the envelope to its transports.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    public static async Task DispatchAsync(
        DispatchEnvelope envelope,
        IServiceProvider entryServices,
        TransportDispatcher transportDispatcher,
        CancellationToken cancellationToken)
    {
        var localDispatcher = entryServices.GetService<ILocalEnvelopeDispatcher>();

        if (localDispatcher is not null && localDispatcher.DeliversLocally(envelope.Type))
        {
            await localDispatcher.PublishLocalEnvelopeAsync(envelope, entryServices, cancellationToken);
        }

        if (localDispatcher is null || !localDispatcher.IsLocalOnly(envelope.Type))
        {
            await transportDispatcher.SendAsync(envelope, localDispatcher, cancellationToken);
        }
    }

    /// <summary>
    /// Disposes the entry's scope once the attempt is over, asynchronously, so a scoped service that implements only
    /// <see cref="IAsyncDisposable"/> is disposed like any other. Never throws: a failure is logged
    /// (<see cref="OutboxLog.ScopeDisposeFailed"/>) and does not retry the entry, and on the failure path it cannot
    /// replace the delivery's exception, which the caller has already logged.
    /// </summary>
    /// <param name="entryScope">The entry's scope.</param>
    /// <param name="logger">The caller's logger.</param>
    /// <param name="entryId">The entry's id.</param>
    /// <param name="type">The entry's message type.</param>
    public static async Task DisposeEntryScopeAsync(AsyncServiceScope entryScope, ILogger logger, long entryId, string type)
    {
        try
        {
            await entryScope.DisposeAsync();
        }
#pragma warning disable CA1031 // The entry's outcome is already written; no disposal failure may change it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            OutboxLog.ScopeDisposeFailed(logger, entryId, type, exception);
        }
    }
}
