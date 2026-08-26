namespace Conduit.Jobs.Persistence;

/// <summary>
/// One unit of coalesced work waiting to become a run. Events sharing a <see cref="ScopeKey"/> merge
/// into this row instead of materializing a run each.
/// </summary>
public sealed class JobScope
{
    /// <summary>
    /// The longest <see cref="ScopeKey"/> accepted. <c>ISqlDialect.KeyTextType</c> is bounded on the
    /// engines that cannot index unbounded text — <c>VARCHAR(255)</c> on MySQL is the tightest — so
    /// this sits comfortably inside it with room for the trigger name.
    /// </summary>
    public const int MaxScopeKeyLength = 200;

    /// <summary><c>{trigger}@{coalesceKey}</c>. Unique, and the reason two hosts opening one scope produce one row.</summary>
    public required string ScopeKey { get; init; }

    /// <summary>
    /// The row's generation identity: a fresh value minted when the scope is opened, and never
    /// changed by a merge. <see cref="Version"/> alone cannot guard a compare-and-set that straddles
    /// a pickup, because a scope's version resets to 0 every time its row is deleted and a new one is
    /// opened under the same <see cref="ScopeKey"/> — two different generations can share a version
    /// number, so the version guard needs <see cref="ScopeId"/> alongside it to tell them apart.
    /// </summary>
    public required string ScopeId { get; init; }

    /// <summary>The declaring trigger. Its own column so the pickup sweep and an operator can group by it.</summary>
    public required string TriggerName { get; init; }

    /// <summary>Full type name of the command this scope will dispatch.</summary>
    public required string MessageType { get; init; }

    /// <summary>The serialized, merged command.</summary>
    public required string Payload { get; init; }

    /// <summary>
    /// When the work becomes due. Written once, on the insert, and never moved by a merge — leading
    /// edge, so a steady stream cannot starve the run.
    /// </summary>
    public DateTimeOffset DueAt { get; init; }

    /// <summary>
    /// Bumped by every merge. It is the optimistic guard that stops one host's merge clobbering
    /// another's — a lost merge here narrows the scope, which loses work rather than duplicating it —
    /// and it is what makes pickup's conditional delete and the run key correct.
    /// </summary>
    public int Version { get; init; }

    /// <summary>Composes and validates a scope key.</summary>
    /// <param name="triggerName">The declaring trigger.</param>
    /// <param name="coalesceKey">The formatted coalesce key.</param>
    /// <returns>The composed key.</returns>
    public static string ComposeKey(string triggerName, string coalesceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(coalesceKey);

        var composed = $"{triggerName}@{coalesceKey}";
        if (composed.Length > MaxScopeKeyLength)
        {
            throw new InvalidOperationException(
                $"The coalesce key for trigger '{triggerName}' composes to {composed.Length} characters, " +
                $"over the {MaxScopeKeyLength} the indexed key column holds. Use a shorter key — an id " +
                "rather than a composite of several fields.");
        }

        return composed;
    }
}
