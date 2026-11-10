using Conduit.Durability.Outbox;
using Conduit.Middleware;
using Conduit.Durability.Configuration;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Persistence;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// Pipeline behavior that writes captured outbound messages to the outbox table
/// within the handler's database transaction.
/// </summary>
/// <remarks>
/// It runs at the end of every pipeline in the dispatch scope — including the nested pipelines of
/// events a handler publishes inline — so it writes only the envelopes that have no row yet
/// (<see cref="OutboxCapture.Unwritten"/>): each captured envelope gets exactly one row per scope,
/// written by the first pipeline to finish after it was captured. An envelope captured after the
/// last behavior in the scope has exited (a host's outermost behavior publishing after its
/// <c>next()</c>) is written by <see cref="IOutboxPendingWriter"/>. When the pipeline faults, the
/// unwritten envelopes it captured are dropped. Each entry carries the dispatch properties
/// <paramref name="options"/> lists (<see cref="OutboxOptions.PersistedProperties"/>); null carries none.
/// <paramref name="timeProvider"/> stamps each entry's <see cref="OutboxEntry.CreatedAt"/>; null is
/// <see cref="TimeProvider.System"/>.
/// </remarks>
public sealed class OutboxBehavior<TRequest, TResult>(
    OutboxCapture capture,
    IOutboxStore store,
    IConduitConnectionFactory connectionFactory,
    IOptions<OutboxOptions>? options = null,
    TimeProvider? timeProvider = null) : IPipelineBehavior<TRequest, TResult>
{
    /// <inheritdoc />
    public async Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        // Envelopes captured from here on belong to this pipeline's unit of work. If it faults —
        // its handler, a nested pipeline it lets the exception escape from, or its own write — those
        // still unwritten are dropped, so a later command in the same DI scope cannot write a failed
        // command's messages into its own transaction. Envelopes captured BEFORE this pipeline began
        // (by an enclosing handler that may catch the exception and carry on) are kept.
        var capturedBefore = capture.Captured.Count;
        try
        {
            var result = await next();
            await OutboxPendingWriter.WriteUnwrittenAsync(
                capture,
                store,
                connectionFactory,
                options?.Value.PersistedProperties,
                timeProvider ?? TimeProvider.System,
                cancellationToken);
            return result;
        }
        catch
        {
            capture.DiscardUnwrittenCapturedSince(capturedBefore);
            throw;
        }
    }
}
