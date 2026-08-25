namespace Conduit.Jobs.Runtime;

/// <summary>
/// The result of one attempt. The failure travels with the outcome rather than on the runner,
/// because a host runs several jobs concurrently and shared last-error state would cross them.
/// </summary>
/// <param name="Outcome">How the attempt ended.</param>
/// <param name="Error">The failure, when the attempt failed.</param>
internal readonly record struct JobAttempt(JobOutcome Outcome, Exception? Error);
