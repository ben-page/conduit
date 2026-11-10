using System.Data;
using System.Data.Common;
using System.Globalization;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Persistence;

namespace Conduit.Durability.Persistence.DeadLetterQueue;

/// <summary>
/// ADO.NET dead letter store. SQL identifiers are module-owned; engine-specific idioms
/// (pagination, type names) come from <see cref="ISqlDialect"/>.
/// </summary>
public sealed class DbDeadLetterStore : DbStoreBase, IDeadLetterStore
{
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

    private readonly string createTableSql;
    private readonly string queryPageSql;

    /// <summary>Initializes a new instance of <see cref="DbDeadLetterStore"/>.</summary>
    public DbDeadLetterStore(IConduitConnectionFactory connectionFactory, ISqlDialect dialect)
        : base(connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(dialect);

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
                DeadLetteredAt {dialect.TimestampType} NOT NULL
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
                DbParameterHelper.Add(command, "@DeadLetteredAt", entry.DeadLetteredAt.ToString("O", CultureInfo.InvariantCulture));
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
        => this.ExecuteNonQueryAsync(this.createTableSql, configureParameters: null, cancellationToken);

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
        // and fail with "could not determine data type of parameter".
        DbParameterHelper.Add(command, "@MessageType", (object?)filter.MessageType ?? DBNull.Value, DbType.String);
        DbParameterHelper.Add(command, "@ExceptionType", (object?)filter.ExceptionType ?? DBNull.Value, DbType.String);
        DbParameterHelper.Add(command, "@Since", filter.Since.HasValue
            ? filter.Since.Value.ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value, DbType.String);
        DbParameterHelper.Add(command, "@Until", filter.Until.HasValue
            ? filter.Until.Value.ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value, DbType.String);
    }
}
