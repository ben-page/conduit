namespace Conduit.Jobs.Persistence;

/// <summary>
/// What one claim renewal learned. Both answers come from a single round-trip: the renewal already
/// had to touch the row, so reading the cancellation flag back costs nothing and needs no poll.
/// </summary>
/// <param name="ClaimHeld">
/// <see langword="false"/> when the claim was taken by another host, in which case this host must
/// stop — it no longer owns the work.
/// </param>
/// <param name="CancellationRequested">Whether an operator has asked for this run to stop.</param>
public readonly record struct HeartbeatResult(bool ClaimHeld, bool CancellationRequested);
