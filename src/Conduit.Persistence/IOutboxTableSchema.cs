namespace Conduit.Persistence;

/// <summary>
/// Table and column name accessors for the outbox table, supplied by the consuming module so the
/// engine's claim builders (<see cref="ISqlDialect.BuildClaimPendingSql"/> and
/// <see cref="ISqlDialect.BuildClaimByIdsSql"/>) can compose the claim SQL against the module's
/// schema. Only the identifiers the claim queries reference are exposed.
/// </summary>
public interface IOutboxTableSchema
{
    /// <summary>The outbox table name.</summary>
    public string TableName { get; }

    /// <summary>The auto-increment primary key column.</summary>
    public string IdColumn { get; }

    /// <summary>The message identifier column.</summary>
    public string MessageIdColumn { get; }

    /// <summary>The status column (Pending = 0, Claiming = 3, etc.).</summary>
    public string StatusColumn { get; }

    /// <summary>The created-at timestamp column.</summary>
    public string CreatedAtColumn { get; }

    /// <summary>The claimed-at timestamp column.</summary>
    public string ClaimedAtColumn { get; }

    /// <summary>The claim-token column.</summary>
    public string ClaimTokenColumn { get; }
}
