using Conduit.Persistence;

namespace Conduit.Durability.Persistence.Outbox;

/// <summary>
/// The durability outbox table/column names, supplied to the engine's claim builders
/// (<see cref="ISqlDialect.BuildClaimPendingSql"/> / <see cref="ISqlDialect.BuildClaimByIdsSql"/>).
/// </summary>
public sealed class OutboxTableSchema : IClaimableTableSchema
{
    private OutboxTableSchema()
    {
    }

    /// <summary>The default outbox schema using Conduit's built-in table and column names.</summary>
    public static OutboxTableSchema Default { get; } = new();

    /// <inheritdoc/>
    public string TableName => "conduit_outbox";

    /// <inheritdoc/>
    public string IdColumn => "Id";

    /// <inheritdoc/>
    public string MessageIdColumn => "MessageId";

    /// <inheritdoc/>
    public string StatusColumn => "Status";

    /// <inheritdoc/>
    public string CreatedAtColumn => "CreatedAt";

    /// <inheritdoc/>
    public string ClaimedAtColumn => "ClaimedAt";

    /// <inheritdoc/>
    public string ClaimTokenColumn => "ClaimToken";

    /// <inheritdoc/>
    public string AttemptCountColumn => "RetryCount";
}
