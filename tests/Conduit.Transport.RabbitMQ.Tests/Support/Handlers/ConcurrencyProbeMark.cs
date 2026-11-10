namespace Conduit.Transport.RabbitMQ.Tests.Support.Handlers;

/// <summary>
/// What <see cref="ConcurrencyProbeCommandHandler"/> records in the <c>SpyLog</c>: a handler entering
/// (<paramref name="Entered"/>) or leaving. Replayed in order, the marks give the most handlers that ran at once.
/// </summary>
public sealed record ConcurrencyProbeMark(string Input, bool Entered)
{
    /// <summary>The most handlers running at once, replaying <paramref name="marks"/> in the order they were recorded.</summary>
    public static int MaxRunningAtOnce(IEnumerable<ConcurrencyProbeMark> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);

        var running = 0;
        var maxRunning = 0;
        foreach (var mark in marks)
        {
            running += mark.Entered ? 1 : -1;
            maxRunning = Math.Max(maxRunning, running);
        }

        return maxRunning;
    }
}
