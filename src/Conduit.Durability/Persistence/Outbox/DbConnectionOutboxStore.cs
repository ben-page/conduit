using System.Data.Common;
using System.Globalization;
using Conduit.Persistence;

namespace Conduit.Durability.Persistence.Outbox;

/// <summary>
/// ADO.NET outbox store. SQL identifiers come from <see cref="OutboxTableSchema"/>; engine-specific
/// idioms (claim SQL, type names) come from <see cref="ISqlDialect"/>. Compatible with any relational
/// database that provides an <see cref="ISqlDialect"/> + <see cref="IConduitConnectionFactory"/>.
/// </summary>
public sealed class DbConnectionOutboxStore : DbStoreBase, IOutboxStore
{
    /// <summary>
    /// How long a claimed-but-unresolved entry stays exclusively claimed before it is treated as
    /// abandoned (e.g. the claiming worker crashed) and becomes re-claimable. This bounds recovery
    /// so a crash never strands a message, while being long enough that a healthy worker always
    /// resolves (sends/retries/fails) its claim well within the window.
    /// </summary>
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);

    private static readonly IClaimableTableSchema Schema = OutboxTableSchema.Default;

    private const string SelectClaimedEntriesSql = """
        SELECT Id, MessageId, ConversationId, CausationId, Type, Payload, Status, CreatedAt, SentAt, RetryCount
        FROM conduit_outbox
        WHERE ClaimToken = @ClaimToken
        ORDER BY Id
        """;

    private const string SelectClaimedIdsSql = """
        SELECT Id, MessageId FROM conduit_outbox
        WHERE ClaimToken = @ClaimToken
        ORDER BY Id
        """;

    private const string InsertEntrySql = """
        INSERT INTO conduit_outbox (MessageId, ConversationId, CausationId, Type, Payload, Status, CreatedAt, RetryCount)
        VALUES (@MessageId, @ConversationId, @CausationId, @Type, @Payload, 0, @CreatedAt, 0)
        """;

    private const string MarkSentSql = "UPDATE conduit_outbox SET Status = 1, SentAt = @SentAt WHERE Id = @Id";

    private const string IncrementRetrySql =
        "UPDATE conduit_outbox SET RetryCount = RetryCount + 1, Status = 0, ClaimToken = NULL, ClaimedAt = NULL WHERE Id = @Id";

    private const string MarkFailedSql = "UPDATE conduit_outbox SET Status = 2 WHERE Id = @Id";

    private const string DeleteCompletedSql =
        "DELETE FROM conduit_outbox WHERE Status = 1 AND CreatedAt <= @RetentionCutoff";

    private const string DeleteFailedSql =
        "DELETE FROM conduit_outbox WHERE Status = 2 AND CreatedAt <= @RetentionCutoff";

    private readonly string createTableSql;
    private readonly string claimPendingSql;
    private readonly string claimByMessageIdsSql;

    /// <summary>Initializes a new instance of <see cref="DbConnectionOutboxStore"/>.</summary>
    public DbConnectionOutboxStore(IConduitConnectionFactory connectionFactory, ISqlDialect dialect)
        : base(connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(dialect);

        this.createTableSql = BuildCreateTableSql(dialect);
        this.claimPendingSql = dialect.BuildClaimPendingSql(Schema);
        this.claimByMessageIdsSql = dialect.BuildClaimByIdsSql(Schema);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(
        IReadOnlyList<OutboxEntry> entries,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(transaction);

        var connection = transaction.Connection
            ?? throw new InvalidOperationException("Transaction has no associated connection.");

        foreach (var entry in entries)
        {
            await ExecuteOnConnectionAsync(
                connection,
                transaction,
                InsertEntrySql,
                command =>
                {
                    DbParameterHelper.Add(command, "@MessageId", entry.MessageId);
                    DbParameterHelper.Add(command, "@ConversationId", entry.ConversationId);
                    DbParameterHelper.Add(command, "@CausationId", (object?)entry.CausationId ?? DBNull.Value);
                    DbParameterHelper.Add(command, "@Type", entry.Type);
                    DbParameterHelper.Add(command, "@Payload", entry.Payload);
                    DbParameterHelper.Add(command, "@CreatedAt", entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
                },
                cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(
        int batchSize,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var graceCutoff = now.Subtract(gracePeriod).ToString("O", CultureInfo.InvariantCulture);
        var claimCutoff = now.Subtract(ClaimTimeout).ToString("O", CultureInfo.InvariantCulture);
        var claimedAt = now.ToString("O", CultureInfo.InvariantCulture);
        var claimToken = Guid.NewGuid().ToString("N");

        // Atomically claim the rows for this worker (Pending or stale-Claiming → Claiming, tagged
        // with our unique token), then read back exactly the rows we claimed under that token.
        // The UPDATE is the exclusivity primitive — two concurrent claimers can never tag the same row.
        await this.ExecuteNonQueryAsync(
            this.claimPendingSql,
            command =>
            {
                DbParameterHelper.Add(command, "@ClaimToken", claimToken);
                DbParameterHelper.Add(command, "@ClaimedAt", claimedAt);
                DbParameterHelper.Add(command, "@GraceCutoff", graceCutoff);
                DbParameterHelper.Add(command, "@ClaimCutoff", claimCutoff);
                DbParameterHelper.Add(command, "@BatchSize", batchSize);
                DbParameterHelper.Add(command, "@PendingStatus", (int)OutboxEntryStatus.Pending);
                DbParameterHelper.Add(command, "@ClaimingStatus", (int)OutboxEntryStatus.Claiming);
            },
            cancellationToken);

        return await this.ExecuteReaderAsync(
            SelectClaimedEntriesSql,
            command => DbParameterHelper.Add(command, "@ClaimToken", claimToken),
            ReadEntry,
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task MarkSentAsync(long entryId, CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(
            MarkSentSql,
            command =>
            {
                DbParameterHelper.Add(command, "@Id", entryId);
                DbParameterHelper.Add(command, "@SentAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task IncrementRetryAsync(long entryId, CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(
            IncrementRetrySql,
            command => DbParameterHelper.Add(command, "@Id", entryId),
            cancellationToken);

    /// <inheritdoc/>
    public Task MarkFailedAsync(long entryId, CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(
            MarkFailedSql,
            command => DbParameterHelper.Add(command, "@Id", entryId),
            cancellationToken);

    /// <inheritdoc/>
    public Task DeleteCompletedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
        => this.DeleteOlderThanAsync(DeleteCompletedSql, retentionPeriod, cancellationToken);

    /// <inheritdoc/>
    public Task DeleteFailedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
        => this.DeleteOlderThanAsync(DeleteFailedSql, retentionPeriod, cancellationToken);

    private async Task DeleteOlderThanAsync(string sql, TimeSpan retentionPeriod, CancellationToken cancellationToken)
    {
        var retentionCutoff = DateTime.UtcNow.Subtract(retentionPeriod).ToString("O", CultureInfo.InvariantCulture);
        await this.ExecuteNonQueryAsync(
            sql,
            command => DbParameterHelper.Add(command, "@RetentionCutoff", retentionCutoff),
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(this.createTableSql, configureParameters: null, cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ClaimedOutboxEntry>> ClaimByMessageIdsAsync(
        IReadOnlyList<string> messageIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageIds);

        if (messageIds.Count == 0)
        {
            return [];
        }

        var now = DateTime.UtcNow;
        var claimCutoff = now.Subtract(ClaimTimeout).ToString("O", CultureInfo.InvariantCulture);
        var claimedAt = now.ToString("O", CultureInfo.InvariantCulture);
        var claimToken = Guid.NewGuid().ToString("N");

        var paramNames = new string[messageIds.Count];
        for (var i = 0; i < messageIds.Count; i++)
        {
            paramNames[i] = $"@p{i}";
        }

        var claimSql = string.Format(
            CultureInfo.InvariantCulture,
            this.claimByMessageIdsSql,
            string.Join(", ", paramNames));

        // Atomically claim the matching rows under our token, then read back what we claimed.
        // The UPDATE guarantees a row is claimed by exactly one worker, so the relay and an
        // immediate-send flush can never both dispatch the same entry.
        await this.ExecuteNonQueryAsync(
            claimSql,
            command =>
            {
                DbParameterHelper.Add(command, "@ClaimToken", claimToken);
                DbParameterHelper.Add(command, "@ClaimedAt", claimedAt);
                DbParameterHelper.Add(command, "@ClaimCutoff", claimCutoff);
                DbParameterHelper.Add(command, "@PendingStatus", (int)OutboxEntryStatus.Pending);
                DbParameterHelper.Add(command, "@ClaimingStatus", (int)OutboxEntryStatus.Claiming);

                for (var i = 0; i < messageIds.Count; i++)
                {
                    DbParameterHelper.Add(command, $"@p{i}", messageIds[i]);
                }
            },
            cancellationToken);

        return await this.ExecuteReaderAsync(
            SelectClaimedIdsSql,
            command => DbParameterHelper.Add(command, "@ClaimToken", claimToken),
            reader => new ClaimedOutboxEntry
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                MessageId = reader.GetString(reader.GetOrdinal("MessageId")),
            },
            cancellationToken);
    }

    private static string BuildCreateTableSql(ISqlDialect dialect)
    {
        var columns = $"""
                Id {dialect.BigIntAutoIncrementPrimaryKey},
                MessageId {dialect.TextType} NOT NULL,
                ConversationId {dialect.TextType} NOT NULL,
                CausationId {dialect.NullableTextType},
                Type {dialect.TextType} NOT NULL,
                Payload {dialect.TextType} NOT NULL,
                Status {dialect.SmallIntType} NOT NULL DEFAULT 0,
                CreatedAt {dialect.TimestampType} NOT NULL,
                SentAt {dialect.NullableTimestampType},
                RetryCount {dialect.IntegerType} NOT NULL DEFAULT 0,
                ClaimedAt {dialect.NullableTimestampType},
                ClaimToken {dialect.NullableTextType}
            """;

        return dialect.BuildCreateTableIfNotExistsSql("conduit_outbox", columns);
    }

    private static OutboxEntry ReadEntry(DbDataReader reader)
    {
        var sentAtOrdinal = reader.GetOrdinal("SentAt");
        var sentAtRaw = reader.IsDBNull(sentAtOrdinal) ? null : reader.GetString(sentAtOrdinal);
        DateTime? sentAt = sentAtRaw is null
            ? null
            : DateTime.Parse(sentAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var causationIdOrdinal = reader.GetOrdinal("CausationId");
        var causationId = reader.IsDBNull(causationIdOrdinal) ? null : reader.GetString(causationIdOrdinal);

        return new OutboxEntry
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            MessageId = reader.GetString(reader.GetOrdinal("MessageId")),
            ConversationId = reader.GetString(reader.GetOrdinal("ConversationId")),
            CausationId = causationId,
            Type = reader.GetString(reader.GetOrdinal("Type")),
            Payload = reader.GetString(reader.GetOrdinal("Payload")),
            // Status maps to the engine's small-int type, whose native CLR width varies (SQL Server
            // TINYINT -> byte, Postgres SMALLINT -> short). GetInt32 only succeeds when the native type
            // is exactly Int32, so convert provider-agnostically.
            Status = (OutboxEntryStatus)Convert.ToInt32(
                reader.GetValue(reader.GetOrdinal("Status")),
                CultureInfo.InvariantCulture),
            CreatedAt = DateTime.Parse(
                reader.GetString(reader.GetOrdinal("CreatedAt")),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            SentAt = sentAt,
            RetryCount = reader.GetInt32(reader.GetOrdinal("RetryCount")),
        };
    }
}
