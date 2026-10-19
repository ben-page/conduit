using System.Collections.Concurrent;
using Conduit.Runtime;

namespace Conduit.Durability.Tests.Support;

/// <summary>
/// A singleton log for the dispatch retry tests: the handler records a <see cref="RetrySighting"/> every time it
/// runs, so a test sees how many attempts ran and which scope each one was resolved from. Thread-safe (jobs and
/// listeners run on background threads).
/// </summary>
public sealed class RetryProbe
{
    private readonly ConcurrentQueue<RetrySighting> sightings = new();

    /// <summary>Every sighting, in order.</summary>
    public IReadOnlyList<RetrySighting> Sightings => [.. this.sightings];

    /// <summary>Records that the handler ran in the scope that resolved <paramref name="scope"/>, and returns how many times it has run.</summary>
    public int See(ScopeProbe scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        this.sightings.Enqueue(new RetrySighting(scope.Id, DispatchAttempt.Current?.Number));
        return this.sightings.Count;
    }

    /// <summary>Polls until at least <paramref name="expected"/> sightings exist, or throws <see cref="TimeoutException"/>.</summary>
    public async Task WaitForAsync(int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (this.sightings.Count < expected)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {expected} sighting(s) but saw {this.sightings.Count}.");
            }

            await Task.Delay(10);
        }
    }
}
