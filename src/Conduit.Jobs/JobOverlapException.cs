namespace Conduit.Jobs;

/// <summary>
/// Thrown by <see cref="IJobs.TriggerNowAsync"/> when the schedule does not allow overlap and a run of
/// it is already outstanding — pending, awaiting a retry, or running — so a manual run would be the
/// second concurrent run the schedule's overlap rule exists to prevent. Nothing was queued.
/// </summary>
/// <remarks>
/// Deliberately not a <c>Conduit.Exceptions.ConflictException</c>: that is a wire refusal, which
/// any handler letting it escape would answer to its client as a 409 with this message. Whether an
/// overlap is the caller's client's business is the caller's decision, so it catches this and
/// throws its own refusal; one left uncaught surfaces as an unexpected failure, which is logged. It
/// is an <see cref="InvalidOperationException"/> because the request is valid and the schedule's
/// current state is what refuses it — retrying once the outstanding run finishes succeeds.
/// </remarks>
public sealed class JobOverlapException : InvalidOperationException
{
    /// <summary>Creates the exception for a schedule whose outstanding run refused a manual one.</summary>
    /// <param name="scheduleName">The schedule that was not triggered.</param>
    public JobOverlapException(string scheduleName)
        : base(
            $"Job schedule '{scheduleName}' already has a run outstanding (pending, awaiting a retry, or " +
            "running), and it does not allow overlap, so no run was queued. Try again once that run " +
            "finishes, or cancel it.")
        => this.ScheduleName = scheduleName;

    /// <summary>The schedule that was not triggered.</summary>
    public string ScheduleName { get; }
}
