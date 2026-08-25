using System.Collections.Concurrent;
using Conduit.Runtime;

namespace Conduit.TestUtils;

/// <summary>
/// Collects the observations emitted during dispatch. Register as a singleton
/// <see cref="IConduitObserver"/> so each test host gets its own isolated instance.
/// Thread-safe — broker transports observe on background consumer threads, so a test
/// thread reading <see cref="Collected"/> races the delivery that produced it.
/// </summary>
public sealed class ObservationCollector : IConduitObserver
{
    private readonly ConcurrentQueue<(MessageObservation Observation, string MessageId, string ConversationId)>
        collected = new();

    public IReadOnlyList<(MessageObservation Observation, string MessageId, string ConversationId)> Collected
        => [.. this.collected];

    public void OnObservation(MessageObservation observation, MessageContext context)
    {
        this.collected.Enqueue((observation, context.MessageId, context.ConversationId));
    }

    public IReadOnlyList<T> OfType<T>() where T : MessageObservation
        => [.. this.collected.Select(c => c.Observation).OfType<T>()];

    /// <summary>
    /// Polls until at least <paramref name="expected"/> observations of type <typeparamref name="T"/>
    /// have been collected, then returns them. Throws <see cref="TimeoutException"/> if the timeout
    /// elapses first, so a test that waits on an observation that never arrives fails rather than hangs.
    /// </summary>
    public async Task<IReadOnlyList<T>> WaitForAsync<T>(int expected = 1, TimeSpan? timeout = null)
        where T : MessageObservation
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var matches = this.OfType<T>();
            if (matches.Count >= expected)
            {
                return matches;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Expected {expected} observation(s) of type {typeof(T).Name} but collected {matches.Count}.");
            }

            await Task.Delay(50);
        }
    }

    public void Clear() => this.collected.Clear();
}
