using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Support;

/// <summary>Helpers for driving a <see cref="FakeTimeProvider"/> until the system reaches a state.</summary>
public static class FakeClock
{
    /// <summary>
    /// Advances in steps until <paramref name="condition"/> holds. A single large jump is not enough
    /// on its own: the loops re-arm their timers asynchronously after each firing, so work that
    /// depends on a later timer needs the clock to keep moving once the earlier one has been served.
    /// </summary>
    public static async Task AdvanceUntilAsync(
        FakeTimeProvider clock,
        TimeSpan step,
        Func<Task<bool>> condition,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"The expected state was never reached; the fake clock reached {clock.GetUtcNow():O}.");
            }

            clock.Advance(step);
            await Task.Delay(20);
        }
    }
}
