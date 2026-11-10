using System.Collections.Concurrent;
using System.Globalization;

namespace Conduit.Runtime;

/// <summary>
/// Pairs each reply a broker transport receives with the request awaiting it, by correlation id.
/// </summary>
/// <remarks>
/// A request is registered <em>before</em> it is published, so a reply that arrives before the
/// publish call returns still finds its waiter: <see cref="Complete"/> on a registered request keeps
/// the reply until <see cref="WaitAsync"/> reads it. The waiter is released when the registration is
/// disposed, after which a late reply is ignored. The wait ends three ways only: the reply, a
/// <see cref="TimeoutException"/> after the response timeout, or an
/// <see cref="OperationCanceledException"/> carrying the caller's own token (and
/// <see cref="FailAll"/>'s exception when the transport stops). The timeout is never a cancellation,
/// so a caller's <c>catch (OperationCanceledException)</c> for its own token cannot swallow it.
/// </remarks>
internal sealed class ReplyCorrelator
{
    private readonly ConcurrentDictionary<string, PendingReply> pending = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;

    /// <summary>Creates a correlator whose response timeouts run on <paramref name="timeProvider"/>.</summary>
    public ReplyCorrelator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
    }

    /// <summary>The requests registered and not yet released.</summary>
    public int PendingCount => this.pending.Count;

    /// <summary>
    /// The exception a request gets when no reply arrives within <paramref name="timeout"/>.
    /// </summary>
    public static TimeoutException NoReply(string messageType, string endpointName, TimeSpan timeout) =>
        new(string.Format(
            CultureInfo.InvariantCulture,
            "No reply to '{0}' from endpoint '{1}' within {2}.",
            messageType,
            endpointName,
            timeout));

    /// <summary>
    /// Registers a request for a reply. Call it before publishing the request; dispose the result
    /// when the request ends, however it ends.
    /// </summary>
    public IDisposable Register(string correlationId, string messageType, string endpointName)
    {
        ArgumentException.ThrowIfNullOrEmpty(correlationId);

        var reply = new PendingReply(this, correlationId, messageType, endpointName);
        if (!this.pending.TryAdd(correlationId, reply))
        {
            throw new InvalidOperationException(
                $"A request with correlation id '{correlationId}' is already awaiting a reply.");
        }

        return reply;
    }

    /// <summary>
    /// Waits for the registered request's reply, at most <paramref name="timeout"/>.
    /// </summary>
    /// <exception cref="TimeoutException">No reply arrived within <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ReadOnlyMemory<byte>> WaitAsync(
        string correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!this.pending.TryGetValue(correlationId, out var reply))
        {
            throw new InvalidOperationException(
                $"No request with correlation id '{correlationId}' is registered for a reply.");
        }

        try
        {
            return await reply.Completion.Task.WaitAsync(timeout, this.timeProvider, cancellationToken);
        }
        catch (TimeoutException) when (!reply.Completion.Task.IsCompleted)
        {
            // WaitAsync's own timeout, not a TimeoutException a FailAll handed over
            throw NoReply(reply.MessageType, reply.EndpointName, timeout);
        }
    }

    /// <summary>
    /// Hands a reply to the request registered under <paramref name="correlationId"/>. The memory must
    /// outlive the call: copy a buffer the broker client reuses. Returns false when no request is
    /// registered under that id (a late or foreign reply) or it already has its answer.
    /// </summary>
    public bool Complete(string correlationId, ReadOnlyMemory<byte> body) =>
        this.pending.TryGetValue(correlationId, out var reply) && reply.Completion.TrySetResult(body);

    /// <summary>Fails every registered request that has no answer yet with <paramref name="exception"/>.</summary>
    public void FailAll(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (var reply in this.pending.Values)
        {
            reply.Completion.TrySetException(exception);
        }
    }

    private sealed class PendingReply(
        ReplyCorrelator owner,
        string correlationId,
        string messageType,
        string endpointName) : IDisposable
    {
        public TaskCompletionSource<ReadOnlyMemory<byte>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string MessageType { get; } = messageType;

        public string EndpointName { get; } = endpointName;

        public void Dispose()
        {
            owner.pending.TryRemove(new KeyValuePair<string, PendingReply>(correlationId, this));

            // A request can end without awaiting its reply (its publish threw, or its transport reads
            // replies another way). Settle it as cancelled, which nothing reports, so a FailAll racing
            // this release cannot leave an unobserved fault behind; observe one it already left.
            if (!this.Completion.TrySetCanceled() && this.Completion.Task.IsFaulted)
            {
                _ = this.Completion.Task.Exception;
            }
        }
    }
}
