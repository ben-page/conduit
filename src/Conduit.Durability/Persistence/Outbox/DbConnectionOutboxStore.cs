using System.Data;
using System.Data.Common;
using System.Globalization;
using Conduit.Durability.Configuration;
using Conduit.Persistence;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Persistence.Outbox;

/// <summary>
/// ADO.NET outbox store. SQL identifiers come from <see cref="OutboxTableSchema"/>; engine-specific
/// idioms (claim SQL, type names) come from <see cref="ISqlDialect"/>. Compatible with any relational
/// database that provides an <see cref="ISqlDialect"/> + <see cref="IConduitConnectionFactory"/>.
/// </summary>
public sealed class DbConnectionOutboxStore : DbStoreBase, IOutboxStore
{
    private static readonly IClaimableTableSchema Schema = OutboxTableSchema.Default;

    // The Properties column is written and read only by a host that lists persisted properties
    // (OutboxOptions.PersistedProperties), so a host that lists none never touches it: its outbox table may
    // predate the column.
    private const string SelectClaimedEntriesSql = """
        SELECT Id, MessageId, ConversationId, CausationId, Type, Payload, Status, CreatedAt, SentAt, RetryCount
        FROM conduit_outbox
        WHERE ClaimToken = @ClaimToken
        ORDER BY Id
        """;

    private const string SelectClaimedEntriesWithPropertiesSql = """
        SELECT Id, MessageId, ConversationId, CausationId, Type, Payload, Status, CreatedAt, SentAt, RetryCount, Properties
        FROM conduit_outbox
        WHERE ClaimToken = @ClaimToken
        ORDER BY Id
        """;

    private const string SelectClaimedIdsSql = """
        SELECT Id, MessageId FROM conduit_outbox
        WHERE ClaimToken = @ClaimToken
        ORDER BY Id
        """;

    // A new entry is due at once: its NextAttemptAt is its CreatedAt, and the relay's grace period is applied to
    // CreatedAt by the claim itself.
    private const string InsertEntrySql = """
        INSERT INTO conduit_outbox (MessageId, ConversationId, CausationId, Type, Payload, Status, CreatedAt, RetryCount, NextAttemptAt)
        VALUES (@MessageId, @ConversationId, @CausationId, @Type, @Payload, 0, @CreatedAt, 0, @NextAttemptAt)
        """;

    private const string InsertEntryWithPropertiesSql = """
        INSERT INTO conduit_outbox (MessageId, ConversationId, CausationId, Type, Payload, Status, CreatedAt, RetryCount, NextAttemptAt, Properties)
        VALUES (@MessageId, @ConversationId, @CausationId, @Type, @Payload, 0, @CreatedAt, 0, @NextAttemptAt, @Properties)
        """;

    // A resolution applies only to a row the caller's claim still holds: Claiming (3), under its token. A claim older
    // than the claim timeout can be taken over by another worker, which stamps its own token, so the first worker's
    // late outcome matches no row: it can neither mark sent nor release back to Pending an entry the new claim is
    // delivering (a release would deliver it a third time). Requiring Claiming also keeps a second resolution under
    // one claim from undoing the first, e.g. a Sent row flipped back to Pending.
    private const string MarkSentSql = """
        UPDATE conduit_outbox SET Status = 1, SentAt = @SentAt
        WHERE Id = @Id AND Status = 3 AND ClaimToken = @ClaimToken
        """;

    private const string IncrementRetrySql = """
        UPDATE conduit_outbox
        SET RetryCount = RetryCount + 1, Status = 0, ClaimToken = NULL, ClaimedAt = NULL, NextAttemptAt = @NextAttemptAt
        WHERE Id = @Id AND Status = 3 AND ClaimToken = @ClaimToken
        """;

    // FailedAt is when the last attempt failed: the Failed retention counts from it.
    private const string MarkFailedSql = """
        UPDATE conduit_outbox SET Status = 2, FailedAt = @FailedAt
        WHERE Id = @Id AND Status = 3 AND ClaimToken = @ClaimToken
        """;

    private const string CountFailedSql = """
        SELECT COUNT(*) FROM conduit_outbox
        WHERE Status = 2 AND (@MessageType IS NULL OR Type = @MessageType)
        """;

    // A retried entry starts over: Pending, its whole retry budget back, due now, and no longer failed.
    private const string RetryFailedByIdSql = """
        UPDATE conduit_outbox
        SET Status = 0, RetryCount = 0, NextAttemptAt = @NextAttemptAt, ClaimToken = NULL, ClaimedAt = NULL, FailedAt = NULL
        WHERE Id = @Id AND Status = 2
        """;

    private const string RetryFailedByTypeSql = """
        UPDATE conduit_outbox
        SET Status = 0, RetryCount = 0, NextAttemptAt = @NextAttemptAt, ClaimToken = NULL, ClaimedAt = NULL, FailedAt = NULL
        WHERE Type = @MessageType AND Status = 2
        """;

    // Retention counts from the outcome, not from the entry's creation: an entry written a week before the broker
    // came back, and sent only then, keeps its full retention.
    private const string DeleteCompletedSql =
        "DELETE FROM conduit_outbox WHERE Status = 1 AND SentAt <= @RetentionCutoff";

    private const string DeleteFailedSql =
        "DELETE FROM conduit_outbox WHERE Status = 2 AND FailedAt <= @RetentionCutoff";

    // The indexes the store's queries use. The pending claim ranges over NextAttemptAt <= now within Status = Pending,
    // and within Status = Claiming for a stale claim (ClaimedAt is a residual there, as the grace predicate on
    // CreatedAt is in both: a row's NextAttemptAt is never before its CreatedAt). The Failed listing, the retries by
    // type and the Failed retention delete use the same index's Status prefix, Failed rows being few. Both claims
    // read their rows back by ClaimToken, the flush claims by MessageId, and the Sent retention delete ranges over
    // SentAt within Status = Sent, the bulk of the table.
    private static readonly TableIndex[] Indexes =
    [
        new("ix_conduit_outbox_status_next_attempt", ["Status", "NextAttemptAt"]),
        new("ix_conduit_outbox_claim", ["ClaimToken"]),
        new("ix_conduit_outbox_message_id", ["MessageId"]),
        new("ix_conduit_outbox_status_sent", ["Status", "SentAt"]),
    ];

    private readonly ISqlDialect dialect;
    private readonly IReadOnlyList<string> createTableSql;
    private readonly string claimPendingSql;
    private readonly string claimByMessageIdsSql;
    private readonly string queryFailedPageSql;
    private readonly bool persistsProperties;

    // A claim older than this counts as abandoned: the pending and the by-id claim both take its row over.
    private readonly TimeSpan claimTimeout;

    // "Now" for every timestamp the store writes and every cutoff it compares against.
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a new instance of <see cref="DbConnectionOutboxStore"/>.</summary>
    /// <param name="connectionFactory">The engine's connection factory.</param>
    /// <param name="dialect">The engine's SQL dialect.</param>
    /// <param name="options">
    /// The outbox options: when <see cref="OutboxOptions.PersistedProperties"/> lists a key, the store writes and
    /// reads the <c>Properties</c> column. Null, or none listed, leaves the column untouched. Its
    /// <see cref="OutboxOptions.ClaimTimeout"/> is how old a claim must be for another worker to take its entry
    /// over; null keeps the default.
    /// </param>
    /// <param name="timeProvider">
    /// The clock the claims, the backoff, the outcome stamps and the retention cutoffs read; null is
    /// <see cref="TimeProvider.System"/>.
    /// </param>
    public DbConnectionOutboxStore(
        IConduitConnectionFactory connectionFactory,
        ISqlDialect dialect,
        IOptions<OutboxOptions>? options = null,
        TimeProvider? timeProvider = null)
        : base(connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(dialect);

        var outboxOptions = options?.Value ?? new OutboxOptions();
        this.persistsProperties = outboxOptions.PersistedProperties.Count > 0;
        this.claimTimeout = outboxOptions.ClaimTimeout;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        this.dialect = dialect;
        this.createTableSql = BuildCreateTableSql(dialect);
        this.claimPendingSql = dialect.BuildClaimPendingSql(Schema);
        this.claimByMessageIdsSql = dialect.BuildClaimByIdsSql(Schema);
        this.queryFailedPageSql = $"""
            SELECT Id, MessageId, ConversationId, CausationId, Type, Payload, Status, CreatedAt, SentAt, RetryCount{(this.persistsProperties ? ", Properties" : string.Empty)}
            FROM conduit_outbox
            WHERE Status = 2 AND (@MessageType IS NULL OR Type = @MessageType)
            ORDER BY Id
            {dialect.PaginationSuffix}
            """;
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
                this.persistsProperties ? InsertEntryWithPropertiesSql : InsertEntrySql,
                command =>
                {
                    DbParameterHelper.Add(command, "@MessageId", entry.MessageId);
                    DbParameterHelper.Add(command, "@ConversationId", entry.ConversationId);
                    DbParameterHelper.Add(command, "@CausationId", (object?)entry.CausationId ?? DBNull.Value);
                    DbParameterHelper.Add(command, "@Type", entry.Type);
                    DbParameterHelper.Add(command, "@Payload", entry.Payload);
                    // In UTC whatever the caller stamped it with: the claim compares it as text with UTC cutoffs.
                    var createdAt = StoredTimestamp.Format(entry.CreatedAt);
                    DbParameterHelper.Add(command, "@CreatedAt", createdAt);
                    DbParameterHelper.Add(command, "@NextAttemptAt", createdAt);
                    if (this.persistsProperties)
                    {
                        DbParameterHelper.Add(command, "@Properties", (object?)entry.Properties ?? DBNull.Value);
                    }
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
        var now = this.timeProvider.GetUtcNow();
        var graceCutoff = StoredTimestamp.Format(now.Subtract(gracePeriod));
        var claimCutoff = StoredTimestamp.Format(now.Subtract(this.claimTimeout));
        var claimedAt = StoredTimestamp.Format(now);
        var claimToken = Guid.NewGuid().ToString("N");

        // Atomically claim the rows for this worker (Pending or stale-Claiming → Claiming, tagged
        // with our unique token), then read back exactly the rows we claimed under that token.
        // The UPDATE is the exclusivity primitive — two concurrent claimers can never tag the same row.
        // A row whose last send failed is not due before its NextAttemptAt (@Now).
        await this.ExecuteNonQueryAsync(
            this.claimPendingSql,
            command =>
            {
                DbParameterHelper.Add(command, "@ClaimToken", claimToken);
                DbParameterHelper.Add(command, "@ClaimedAt", claimedAt);
                DbParameterHelper.Add(command, "@GraceCutoff", graceCutoff);
                DbParameterHelper.Add(command, "@Now", claimedAt);
                DbParameterHelper.Add(command, "@ClaimCutoff", claimCutoff);
                DbParameterHelper.Add(command, "@BatchSize", batchSize);
                DbParameterHelper.Add(command, "@PendingStatus", (int)OutboxEntryStatus.Pending);
                DbParameterHelper.Add(command, "@ClaimingStatus", (int)OutboxEntryStatus.Claiming);
            },
            cancellationToken);

        var withProperties = this.persistsProperties;
        return await this.ExecuteReaderAsync(
            withProperties ? SelectClaimedEntriesWithPropertiesSql : SelectClaimedEntriesSql,
            command => DbParameterHelper.Add(command, "@ClaimToken", claimToken),
            reader => ReadEntry(reader, withProperties, claimToken),
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> MarkSentAsync(long entryId, string claimToken, CancellationToken cancellationToken)
        => this.ResolveAsync(
            MarkSentSql,
            entryId,
            claimToken,
            command => DbParameterHelper.Add(command, "@SentAt", this.Now()),
            cancellationToken);

    /// <inheritdoc/>
    public Task<bool> IncrementRetryAsync(
        long entryId,
        string claimToken,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
        => this.ResolveAsync(
            IncrementRetrySql,
            entryId,
            claimToken,
            command => DbParameterHelper.Add(
                command,
                "@NextAttemptAt",
                StoredTimestamp.Format(this.timeProvider.GetUtcNow().Add(retryDelay))),
            cancellationToken);

    /// <inheritdoc/>
    public Task<bool> MarkFailedAsync(long entryId, string claimToken, CancellationToken cancellationToken)
        => this.ResolveAsync(
            MarkFailedSql,
            entryId,
            claimToken,
            command => DbParameterHelper.Add(command, "@FailedAt", this.Now()),
            cancellationToken);

    /// <inheritdoc/>
    public async Task<FailedOutboxPage> QueryFailedAsync(FailedOutboxFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // SQL Server refuses a FETCH of no rows, and a page is never the whole table.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(filter.PageSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(filter.PageSize, FailedOutboxFilter.MaxPageSize);

        var offset = 0;
        if (filter.PageToken is not null && int.TryParse(filter.PageToken, CultureInfo.InvariantCulture, out var parsedOffset))
        {
            offset = parsedOffset;
        }

        var totalCount = Convert.ToInt32(
            await this.ExecuteScalarAsync(CountFailedSql, command => AddMessageTypeFilter(command, filter.MessageType), cancellationToken),
            CultureInfo.InvariantCulture);

        var withProperties = this.persistsProperties;
        var entries = await this.ExecuteReaderAsync(
            this.queryFailedPageSql,
            command =>
            {
                AddMessageTypeFilter(command, filter.MessageType);
                DbParameterHelper.Add(command, "@PageSize", filter.PageSize);
                DbParameterHelper.Add(command, "@Offset", offset);
            },
            reader => ReadEntry(reader, withProperties, claimToken: null),
            cancellationToken);

        var nextOffset = offset + entries.Count;
        return new FailedOutboxPage
        {
            Entries = entries,
            NextPageToken = nextOffset < totalCount ? nextOffset.ToString(CultureInfo.InvariantCulture) : null,
            TotalCount = totalCount,
        };
    }

    /// <inheritdoc/>
    public async Task<bool> RetryFailedAsync(long entryId, CancellationToken cancellationToken)
        => await this.ExecuteNonQueryAsync(
            RetryFailedByIdSql,
            command =>
            {
                DbParameterHelper.Add(command, "@Id", entryId);
                DbParameterHelper.Add(command, "@NextAttemptAt", this.Now());
            },
            cancellationToken) > 0;

    /// <inheritdoc/>
    public Task<int> RetryFailedAsync(string messageType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);

        return this.ExecuteNonQueryAsync(
            RetryFailedByTypeSql,
            command =>
            {
                DbParameterHelper.Add(command, "@MessageType", messageType);
                DbParameterHelper.Add(command, "@NextAttemptAt", this.Now());
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task DeleteCompletedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
        => this.DeleteOlderThanAsync(DeleteCompletedSql, retentionPeriod, cancellationToken);

    /// <inheritdoc/>
    public Task DeleteFailedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
        => this.DeleteOlderThanAsync(DeleteFailedSql, retentionPeriod, cancellationToken);

    private async Task DeleteOlderThanAsync(string sql, TimeSpan retentionPeriod, CancellationToken cancellationToken)
    {
        var retentionCutoff = StoredTimestamp.Format(this.timeProvider.GetUtcNow().Subtract(retentionPeriod));
        await this.ExecuteNonQueryAsync(
            sql,
            command => DbParameterHelper.Add(command, "@RetentionCutoff", retentionCutoff),
            cancellationToken);
    }

    // The clock's current instant as stored text.
    private string Now() => StoredTimestamp.Format(this.timeProvider.GetUtcNow());

    // Runs one of the claim-guarded resolutions (MarkSentSql, IncrementRetrySql, MarkFailedSql): true when it changed
    // the row, false when the claim no longer holds it.
    private async Task<bool> ResolveAsync(
        string sql,
        long entryId,
        string claimToken,
        Action<DbCommand>? bindValues,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

        var rowsAffected = await this.ExecuteNonQueryAsync(
            sql,
            command =>
            {
                DbParameterHelper.Add(command, "@Id", entryId);
                DbParameterHelper.Add(command, "@ClaimToken", claimToken);
                bindValues?.Invoke(command);
            },
            cancellationToken);

        return rowsAffected == 1;
    }

    /// <inheritdoc/>
    // The table, then its indexes (where the engine creates them separately), in order; one another replica created
    // first is passed over.
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken)
        => this.EnsureCreatedAsync(this.dialect, this.createTableSql, cancellationToken);

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

        var now = this.timeProvider.GetUtcNow();
        var claimCutoff = StoredTimestamp.Format(now.Subtract(this.claimTimeout));
        var claimedAt = StoredTimestamp.Format(now);
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
        // immediate-send flush can never both dispatch the same entry. Only a never-attempted row is
        // eligible: a flush that runs later than the grace period, after the relay has already tried and
        // released the row, leaves it to the relay, whose retries are the ones MaxRetries bounds.
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
                ClaimToken = claimToken,
            },
            cancellationToken);
    }

    // An indexed column is KeyTextType: on SQL Server and MySQL the general text and timestamp types are unbounded
    // and cannot be index keys. That includes the two indexed timestamps, NextAttemptAt and SentAt, whose ISO 8601
    // text fits the bounded type.
    private static IReadOnlyList<string> BuildCreateTableSql(ISqlDialect dialect)
    {
        var columns = $"""
                Id {dialect.BigIntAutoIncrementPrimaryKey},
                MessageId {dialect.KeyTextType} NOT NULL,
                ConversationId {dialect.TextType} NOT NULL,
                CausationId {dialect.NullableTextType},
                Type {dialect.TextType} NOT NULL,
                Payload {dialect.TextType} NOT NULL,
                Status {dialect.SmallIntType} NOT NULL DEFAULT 0,
                CreatedAt {dialect.TimestampType} NOT NULL,
                SentAt {dialect.KeyTextType},
                FailedAt {dialect.NullableTimestampType},
                RetryCount {dialect.IntegerType} NOT NULL DEFAULT 0,
                NextAttemptAt {dialect.KeyTextType} NOT NULL,
                ClaimedAt {dialect.NullableTimestampType},
                ClaimToken {dialect.KeyTextType},
                Properties {dialect.NullableTextType}
            """;

        return dialect.BuildCreateTableIfNotExistsSql("conduit_outbox", columns, Indexes);
    }

    // Bound with an explicit string type: a null filter must still carry one, since Npgsql cannot infer the type of
    // an untyped DBNull in an IS NULL predicate.
    private static void AddMessageTypeFilter(DbCommand command, string? messageType)
        => DbParameterHelper.Add(command, "@MessageType", (object?)messageType ?? DBNull.Value, DbType.String);

    // claimToken is the claim that read the row back (it equals the row's ClaimToken column), or null for a row no claim
    // returned.
    private static OutboxEntry ReadEntry(DbDataReader reader, bool withProperties, string? claimToken)
    {
        var sentAtOrdinal = reader.GetOrdinal("SentAt");
        var sentAtRaw = reader.IsDBNull(sentAtOrdinal) ? null : reader.GetString(sentAtOrdinal);
        DateTime? sentAt = sentAtRaw is null
            ? null
            : DateTime.Parse(sentAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var causationIdOrdinal = reader.GetOrdinal("CausationId");
        var causationId = reader.IsDBNull(causationIdOrdinal) ? null : reader.GetString(causationIdOrdinal);

        string? properties = null;
        if (withProperties)
        {
            var propertiesOrdinal = reader.GetOrdinal("Properties");
            properties = reader.IsDBNull(propertiesOrdinal) ? null : reader.GetString(propertiesOrdinal);
        }

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
            Properties = properties,
            ClaimToken = claimToken,
        };
    }
}
