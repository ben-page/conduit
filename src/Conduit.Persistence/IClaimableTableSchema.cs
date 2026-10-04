namespace Conduit.Persistence;

/// <summary>
/// Table and column accessors for any table participating in the atomic claim protocol, supplied by
/// the consuming module so the engine's claim builders
/// (<see cref="ISqlDialect.BuildClaimPendingSql"/> and <see cref="ISqlDialect.BuildClaimByIdsSql"/>)
/// can compose the claim SQL against its schema. Only the identifiers the claim queries reference are
/// exposed. The durability outbox and the jobs run ledger both implement it.
/// </summary>
/// <remarks>
/// <see cref="CreatedAtColumn"/> is the claim's <b>eligibility</b> column, compared against
/// <c>@GraceCutoff</c> — it is not required to mean "created at". The jobs ledger maps it to the
/// run's due time, which is what turns "claim pending entries" into "claim jobs that are due".
/// </remarks>
public interface IClaimableTableSchema
{
    /// <summary>The claimable table's name.</summary>
    public string TableName { get; }

    /// <summary>The auto-increment primary key column, which also orders claims.</summary>
    public string IdColumn { get; }

    /// <summary>The message identifier column matched by <see cref="ISqlDialect.BuildClaimByIdsSql"/>.</summary>
    public string MessageIdColumn { get; }

    /// <summary>The status column (Pending = 0, Claiming = 3, etc.).</summary>
    public string StatusColumn { get; }

    /// <summary>
    /// The eligibility timestamp column, compared against <c>@GraceCutoff</c>. The outbox maps it to
    /// the entry's created-at; the jobs ledger maps it to the run's due time.
    /// </summary>
    public string CreatedAtColumn { get; }

    /// <summary>The claimed-at timestamp column, which a stale claim is detected from.</summary>
    public string ClaimedAtColumn { get; }

    /// <summary>The claim-token column identifying the worker holding the claim.</summary>
    public string ClaimTokenColumn { get; }

    /// <summary>
    /// The count of failed attempts already made on the row. <see cref="ISqlDialect.BuildClaimByIdsSql"/>
    /// claims only rows where it is zero: a by-id claim is a first attempt, never a retry.
    /// </summary>
    public string AttemptCountColumn { get; }
}
