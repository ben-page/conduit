using System.Data;
using System.Data.Common;
using System.Globalization;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Persistence;

namespace Conduit.Durability.Persistence.DeadLetterQueue;

/// <summary>
/// ADO.NET dead letter store. SQL identifiers are module-owned; engine-specific idioms
/// (pagination, type names) come from <see cref="ISqlDialect"/>. Timestamps are written, and the
/// <see cref="DeadLetterFilter.Since"/>/<see cref="DeadLetterFilter.Until"/> bounds bound, as UTC text, so the
/// text comparison orders instants whatever offset the caller used.
/// </summary>
public sealed class DbDeadLetterStore : DbStoreBase, IDeadLetterStore
{
    /// <summary>
    /// How old a replay claim must be before another replay may take the entry over: the replay that made it is taken to
    /// have stopped between its claim and its removal. A replay is one broker publish and a delete, so five minutes is
    /// long past any live one.
    /// </summary>
    internal static readonly TimeSpan ReplayClaimTimeout = TimeSpan.FromMinutes(5);

    // A claim is one statement on every engine: it changes the row only when no live claim holds it, and the rows it
    // changed (1 or 0) say whether this replay holds the entry now.
    private const string ClaimForReplaySql = """
        UPDATE conduit_dead_letters SET ReplayClaimToken = @ClaimToken, ReplayClaimedAt = @ClaimedAt
        WHERE Id = @Id AND (ReplayClaimToken IS NULL OR ReplayClaimedAt <= @ClaimCutoff)
        """;

    // Only the holder's own claim: a replay whose claim was taken over must not free the entry under the new holder.
    private const string ReleaseReplayClaimSql = """
        UPDATE conduit_dead_letters SET ReplayClaimToken = NULL, ReplayClaimedAt = NULL
        WHERE Id = @Id AND ReplayClaimToken = @ClaimToken
        """;

    private const string InsertSql = """
        INSERT INTO conduit_dead_letters (Id, MessageType, Body, SourceEndpoint, ExceptionType, ExceptionMessage, Reference, MessageId, CausationId, ConversationId, AttemptCount, DeadLetteredAt)
        VALUES (@Id, @MessageType, @Body, @SourceEndpoint, @ExceptionType, @ExceptionMessage, @Reference, @MessageId, @CausationId, @ConversationId, @AttemptCount, @DeadLetteredAt)
        """;

    private const string SelectByIdSql = """
        SELECT Id, MessageType, Body, SourceEndpoint, ExceptionType, ExceptionMessage, Reference, MessageId, CausationId, ConversationId, AttemptCount, DeadLetteredAt
        FROM conduit_dead_letters WHERE Id = @Id
        """;

    private const string CountSql = """
        SELECT COUNT(*) FROM conduit_dead_letters
        WHERE (@MessageType IS NULL OR MessageType = @MessageType)
          AND (@ExceptionType IS NULL OR ExceptionType = @ExceptionType)
          AND (@Since IS NULL OR DeadLetteredAt >= @Since)
          AND (@Until IS NULL OR DeadLetteredAt <= @Until)
        """;

    private const string DeleteByIdSql = "DELETE FROM conduit_dead_letters WHERE Id = @Id";

    private readonly ISqlDialect dialect;
    private readonly string createTableSql;
    private readonly string queryPageSql;
    private readonly string claimForReplaySql;
    private readonly string releaseReplayClaimSql;
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a new instance of <see cref="DbDeadLetterStore"/>.</summary>
    /// <param name="connectionFactory">The engine's connection factory.</param>
    /// <param name="dialect">The engine's SQL dialect.</param>
    /// <param name="timeProvider">
    /// The clock a replay claim's time and its abandonment cutoff read; null is <see cref="TimeProvider.System"/>.
    /// </param>
    public DbDeadLetterStore(
        IConduitConnectionFactory connectionFactory,
        ISqlDialect dialect,
        TimeProvider? timeProvider = null)
        : base(connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(dialect);

        this.dialect = dialect;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        // Replays race on one row: the loser must wait for the winner's update and find the claim taken, not fail as it
        // would at a Snapshot level a SQL Server session can carry.
        this.claimForReplaySql = dialect.BuildReadCommittedSql(ClaimForReplaySql);
        this.releaseReplayClaimSql = dialect.BuildReadCommittedSql(ReleaseReplayClaimSql);
        this.createTableSql = dialect.BuildCreateTableIfNotExistsSql(
            "conduit_dead_letters",
            $"""
                Id {dialect.KeyTextType} NOT NULL PRIMARY KEY,
                MessageType {dialect.TextType} NOT NULL,
                Body {dialect.BlobType} NOT NULL,
                SourceEndpoint {dialect.TextType} NOT NULL,
                ExceptionType {dialect.NullableTextType},
                ExceptionMessage {dialect.NullableTextType},
                Reference {dialect.NullableTextType},
                MessageId {dialect.NullableTextType},
                CausationId {dialect.NullableTextType},
                ConversationId {dialect.NullableTextType},
                AttemptCount {dialect.IntegerType} NOT NULL DEFAULT 1,
                DeadLetteredAt {dialect.TimestampType} NOT NULL,
                ReplayClaimToken {dialect.NullableTextType},
                ReplayClaimedAt {dialect.NullableTimestampType}
            """);

        this.queryPageSql = $"""
            SELECT Id, MessageType, Body, SourceEndpoint, ExceptionType, ExceptionMessage, Reference, MessageId, CausationId, ConversationId, AttemptCount, DeadLetteredAt
            FROM conduit_dead_letters
            WHERE (@MessageType IS NULL OR MessageType = @MessageType)
              AND (@ExceptionType IS NULL OR ExceptionType = @ExceptionType)
              AND (@Since IS NULL OR DeadLetteredAt >= @Since)
              AND (@Until IS NULL OR DeadLetteredAt <= @Until)
            ORDER BY DeadLetteredAt DESC
            {dialect.PaginationSuffix}
            """;
    }

    /// <inheritdoc/>
    public Task WriteAsync(DeadLetterEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return this.ExecuteNonQueryAsync(
            InsertSql,
            command =>
            {
                DbParameterHelper.Add(command, "@Id", entry.Id.ToString("D"));
                DbParameterHelper.Add(command, "@MessageType", entry.MessageType);
                DbParameterHelper.Add(command, "@Body", entry.Body.ToArray());
                DbParameterHelper.Add(command, "@SourceEndpoint", entry.SourceEndpoint);
                DbParameterHelper.Add(command, "@ExceptionType", (object?)entry.ExceptionType ?? DBNull.Value);
                DbParameterHelper.Add(command, "@ExceptionMessage", (object?)entry.ExceptionMessage ?? DBNull.Value);
                DbParameterHelper.Add(command, "@Reference", (object?)entry.Reference ?? DBNull.Value);
                DbParameterHelper.Add(command, "@MessageId", (object?)entry.MessageId ?? DBNull.Value);
                DbParameterHelper.Add(command, "@CausationId", (object?)entry.CausationId ?? DBNull.Value);
                DbParameterHelper.Add(command, "@ConversationId", (object?)entry.ConversationId ?? DBNull.Value);
                DbParameterHelper.Add(command, "@AttemptCount", entry.AttemptCount);
                DbParameterHelper.Add(command, "@DeadLetteredAt", StoredTimestamp.Format(entry.DeadLetteredAt));
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<DeadLetterEntry?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var entries = await this.ExecuteReaderAsync(
            SelectByIdSql,
            command => DbParameterHelper.Add(command, "@Id", id.ToString("D")),
            ReadEntry,
            cancellationToken);
        return entries.Count == 0 ? null : entries[0];
    }

    /// <inheritdoc/>
    public async Task<DeadLetterPage> QueryAsync(DeadLetterFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // SQL Server refuses a FETCH of no rows, and a page is never the whole table.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(filter.PageSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(filter.PageSize, DeadLetterFilter.MaxPageSize);

        var offset = 0;
        if (filter.PageToken is not null && int.TryParse(filter.PageToken, CultureInfo.InvariantCulture, out var parsedOffset))
        {
            offset = parsedOffset;
        }

        var countResult = await this.ExecuteScalarAsync(
            CountSql,
            command => AddFilterParameters(command, filter),
            cancellationToken);
        var totalCount = Convert.ToInt32(countResult, CultureInfo.InvariantCulture);

        var entries = await this.ExecuteReaderAsync(
            this.queryPageSql,
            command =>
            {
                AddFilterParameters(command, filter);
                DbParameterHelper.Add(command, "@PageSize", filter.PageSize);
                DbParameterHelper.Add(command, "@Offset", offset);
            },
            ReadEntry,
            cancellationToken);

        var nextOffset = offset + entries.Count;
        var nextPageToken = nextOffset < totalCount
            ? nextOffset.ToString(CultureInfo.InvariantCulture)
            : null;

        return new DeadLetterPage
        {
            Entries = entries,
            NextPageToken = nextPageToken,
            TotalCount = totalCount,
        };
    }

    /// <inheritdoc/>
    /// <remarks>A claim older than five minutes (<see cref="ReplayClaimTimeout"/>) is abandoned.</remarks>
    public async Task<string?> ClaimForReplayAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = this.timeProvider.GetUtcNow();
        var claimToken = Guid.NewGuid().ToString("N");

        var claimed = await this.ExecuteNonQueryAsync(
            this.claimForReplaySql,
            command =>
            {
                DbParameterHelper.Add(command, "@Id", id.ToString("D"));
                DbParameterHelper.Add(command, "@ClaimToken", claimToken);
                DbParameterHelper.Add(command, "@ClaimedAt", StoredTimestamp.Format(now));
                DbParameterHelper.Add(command, "@ClaimCutoff", StoredTimestamp.Format(now - ReplayClaimTimeout));
            },
            cancellationToken);

        return claimed == 1 ? claimToken : null;
    }

    /// <inheritdoc/>
    public Task ReleaseReplayClaimAsync(Guid id, string claimToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

        return this.ExecuteNonQueryAsync(
            this.releaseReplayClaimSql,
            command =>
            {
                DbParameterHelper.Add(command, "@Id", id.ToString("D"));
                DbParameterHelper.Add(command, "@ClaimToken", claimToken);
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task RemoveAsync(Guid id, CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(
            DeleteByIdSql,
            command => DbParameterHelper.Add(command, "@Id", id.ToString("D")),
            cancellationToken);

    /// <inheritdoc/>
    public async Task RemoveAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return;
        }

        var paramNames = new string[idList.Count];
        for (var i = 0; i < idList.Count; i++)
        {
            paramNames[i] = $"@p{i}";
        }

        var deleteSql = string.Format(
            CultureInfo.InvariantCulture,
            "DELETE FROM conduit_dead_letters WHERE Id IN ({0})",
            string.Join(", ", paramNames));

        await this.ExecuteNonQueryAsync(
            deleteSql,
            command =>
            {
                for (var i = 0; i < idList.Count; i++)
                {
                    DbParameterHelper.Add(command, paramNames[i], idList[i].ToString("D"));
                }
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken)
        => this.EnsureCreatedAsync(this.dialect, [this.createTableSql], cancellationToken);

    private static DeadLetterEntry ReadEntry(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
        MessageType = reader.GetString(reader.GetOrdinal("MessageType")),
        Body = new BinaryData(GetBytes(reader, reader.GetOrdinal("Body"))),
        SourceEndpoint = reader.GetString(reader.GetOrdinal("SourceEndpoint")),
        ExceptionType = reader.IsDBNull(reader.GetOrdinal("ExceptionType")) ? null : reader.GetString(reader.GetOrdinal("ExceptionType")),
        ExceptionMessage = reader.IsDBNull(reader.GetOrdinal("ExceptionMessage")) ? null : reader.GetString(reader.GetOrdinal("ExceptionMessage")),
        Reference = reader.IsDBNull(reader.GetOrdinal("Reference")) ? null : reader.GetString(reader.GetOrdinal("Reference")),
        MessageId = reader.IsDBNull(reader.GetOrdinal("MessageId")) ? null : reader.GetString(reader.GetOrdinal("MessageId")),
        CausationId = reader.IsDBNull(reader.GetOrdinal("CausationId")) ? null : reader.GetString(reader.GetOrdinal("CausationId")),
        ConversationId = reader.IsDBNull(reader.GetOrdinal("ConversationId")) ? null : reader.GetString(reader.GetOrdinal("ConversationId")),
        AttemptCount = reader.GetInt32(reader.GetOrdinal("AttemptCount")),
        DeadLetteredAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("DeadLetteredAt")), CultureInfo.InvariantCulture),
    };

    private static byte[] GetBytes(DbDataReader reader, int ordinal)
    {
        using var stream = reader.GetStream(ordinal);
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static void AddFilterParameters(DbCommand command, DeadLetterFilter filter)
    {
        // All four filter values are text (message/exception type names, ISO 8601 timestamps). Bind
        // them with an explicit string DbType so a null filter still carries a resolvable type — some
        // providers (Npgsql) cannot infer the type of an untyped DBNull used in an `IS NULL` predicate
        // and fail with "could not determine data type of parameter". The bounds are compared as text
        // with the stored UTC values, so they are written in UTC too: a caller's own offset would compare
        // by its wall-clock digits.
        DbParameterHelper.Add(command, "@MessageType", (object?)filter.MessageType ?? DBNull.Value, DbType.String);
        DbParameterHelper.Add(command, "@ExceptionType", (object?)filter.ExceptionType ?? DBNull.Value, DbType.String);
        DbParameterHelper.Add(command, "@Since", filter.Since.HasValue
            ? StoredTimestamp.Format(filter.Since.Value)
            : DBNull.Value, DbType.String);
        DbParameterHelper.Add(command, "@Until", filter.Until.HasValue
            ? StoredTimestamp.Format(filter.Until.Value)
            : DBNull.Value, DbType.String);
    }
}
