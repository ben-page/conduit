using Conduit.Runtime;

namespace Conduit.Tests.Support;

/// <summary>
/// A singleton log for the dispatch retry tests: each handler and behavior records a <see cref="RetryProbeSighting"/>
/// every time it runs, so a test sees how many attempts ran and which scope each one was resolved from. Thread-safe
/// (a fire-and-forget handler records on the background dispatcher).
/// </summary>
public sealed class RetryProbe
{
    private readonly Lock gate = new();
    private readonly List<RetryProbeSighting> sightings = [];

    /// <summary>
    /// Records that <paramref name="site"/> ran in the scope that resolved <paramref name="scope"/>, and returns how
    /// many times it has run, this time included.
    /// </summary>
    public int See(string site, AttemptProbe scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var sighting = new RetryProbeSighting(
            site, scope.Id, DispatchAttempt.Current?.Number, MessageContext.Current?.MessageId);
        lock (this.gate)
        {
            this.sightings.Add(sighting);
            return this.sightings.Count(seen => seen.Site == site);
        }
    }

    /// <summary>Every sighting of <paramref name="site"/>, in order.</summary>
    public IReadOnlyList<RetryProbeSighting> At(string site)
    {
        lock (this.gate)
        {
            return [.. this.sightings.Where(seen => seen.Site == site)];
        }
    }

    /// <summary>Every sighting, in order.</summary>
    public IReadOnlyList<RetryProbeSighting> All()
    {
        lock (this.gate)
        {
            return [.. this.sightings];
        }
    }

    /// <summary>
    /// Polls until <paramref name="site"/> has run at least <paramref name="expected"/> times, or throws
    /// <see cref="TimeoutException"/>.
    /// </summary>
    public async Task WaitForAsync(string site, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (this.At(site).Count < expected)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {expected} sighting(s) of {site} but saw {this.At(site).Count}.");
            }

            await Task.Delay(10);
        }
    }
}
