namespace Conduit.Jobs;

/// <summary>
/// One unit of coalesced work waiting to become a run. Its own record rather than a
/// <see cref="JobRunInfo"/>: a scope has no id, no status and no attempt count, and folding it in
/// would mean synthesizing a run key that <c>CancelAsync</c> would silently do nothing with, because
/// no run row holds it yet.
/// </summary>
/// <param name="TriggerName">The declaring event trigger.</param>
/// <param name="CoalesceKey">The key this work coalesces on.</param>
/// <param name="MessageType">The command the scope will dispatch.</param>
/// <param name="DueAt">When the work becomes due. Leading-edge, so merging never moves it.</param>
/// <param name="Version">
/// How many merges this scope has absorbed. Climbing while <paramref name="DueAt"/> holds still is
/// the signal that a coalescing trigger is doing its job.
/// </param>
public sealed record JobScopeInfo(
    string TriggerName,
    string CoalesceKey,
    string MessageType,
    DateTimeOffset DueAt,
    int Version);
