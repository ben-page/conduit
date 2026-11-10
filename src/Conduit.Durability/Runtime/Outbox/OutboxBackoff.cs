using Conduit.Durability.Configuration;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// How long an outbox entry waits for its next send attempt: <see cref="OutboxOptions.InitialBackoff"/> after
/// its first failed attempt, doubled with each failed attempt after that, never more than
/// <see cref="OutboxOptions.MaxBackoff"/>. The relay and the post-commit flush both use it.
/// </summary>
internal static class OutboxBackoff
{
    /// <summary>The wait after <paramref name="failedAttempts"/> failed attempts.</summary>
    /// <param name="options">The outbox options holding the two bounds.</param>
    /// <param name="failedAttempts">The failed attempts so far, the one just made included: 1 after a first failure.</param>
    /// <returns>The wait before the entry is due again.</returns>
    public static TimeSpan Delay(OutboxOptions options, int failedAttempts)
    {
        var delay = options.InitialBackoff;
        for (var attempt = 1; attempt < failedAttempts && delay > TimeSpan.Zero && delay < options.MaxBackoff; attempt++)
        {
            // Doubling can overflow TimeSpan only past MaxBackoff, so the ceiling is taken before it would.
            delay = delay > options.MaxBackoff - delay ? options.MaxBackoff : delay + delay;
        }

        return delay < options.MaxBackoff ? delay : options.MaxBackoff;
    }
}
