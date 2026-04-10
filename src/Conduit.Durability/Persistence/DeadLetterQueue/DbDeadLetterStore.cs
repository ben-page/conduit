using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Conduit.DeadLetterQueue;
using Conduit.Durability.Persistence.Dialects;

namespace Conduit.Durability.Persistence.DeadLetterQueue;

/// <summary>
/// ADO.NET dead letter store implementation. Delegates all SQL to <see cref="IDurabilityDialect"/>,
/// making it compatible with any relational database that provides a <see cref="DbConnection"/>.
/// </summary>
// CA2100: All SQL in this class comes from IDurabilityDialect — a trusted internal interface, never user input.
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "All SQL originates from IDurabilityDialect, which is internal infrastructure, never user-supplied input.")]
public sealed class DbDeadLetterStore : IDeadLetterStore
{
    private readonly IDurabilityConnectionFactory connectionFactory;
    private readonly IDurabilityDialect dialect;

    /// <summary>
    /// Initializes a new instance of <see cref="DbDeadLetterStore"/>.
    /// </summary>
    public DbDeadLetterStore(IDurabilityConnectionFactory connectionFactory, IDurabilityDialect dialect)
    {
        this.connectionFactory = connectionFactory;
        this.dialect = dialect;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(DeadLetterEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var connection = await this.connectionFactory.CreateConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = this.dialect.DeadLetterInsertSql;

        AddParameter(command, "@Id", entry.Id.ToString("D"));
        AddParameter(command, "@MessageType", entry.MessageType);
        AddParameter(command, "@Body", entry.Body.ToArray());
        AddParameter(command, "@SourceEndpoint", entry.SourceEndpoint);
        AddParameter(command, "@ExceptionType", (object?)entry.ExceptionType ?? DBNull.Value);
        AddParameter(command, "@ExceptionMessage", (object?)entry.ExceptionMessage ?? DBNull.Value);
        AddParameter(command, "@MessageId", (object?)entry.MessageId ?? DBNull.Value);
        AddParameter(command, "@CausationId", (object?)entry.CausationId ?? DBNull.Value);
        AddParameter(command, "@ConversationId", (object?)entry.ConversationId ?? DBNull.Value);
        AddParameter(command, "@AttemptCount", entry.AttemptCount);
        AddParameter(command, "@DeadLetteredAt", entry.DeadLetteredAt.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<DeadLetterEntry?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await this.connectionFactory.CreateConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = this.dialect.DeadLetterSelectByIdSql;
        AddParameter(command, "@Id", id.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadEntry(reader);
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

        var connection = await this.connectionFactory.CreateConnectionAsync(cancellationToken);

        // Count
        int totalCount;
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = this.dialect.DeadLetterCountSql;
            AddFilterParameters(countCommand, filter);
            var countResult = await countCommand.ExecuteScalarAsync(cancellationToken);
            totalCount = Convert.ToInt32(countResult, CultureInfo.InvariantCulture);
        }

        // Page
        var entries = new List<DeadLetterEntry>();
        using (var pageCommand = connection.CreateCommand())
        {
            pageCommand.CommandText = this.dialect.DeadLetterQueryPageSql;
            AddFilterParameters(pageCommand, filter);
            AddParameter(pageCommand, "@PageSize", filter.PageSize);
            AddParameter(pageCommand, "@Offset", offset);

            await using var reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add(ReadEntry(reader));
            }
        }

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
    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await this.connectionFactory.CreateConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = this.dialect.DeadLetterDeleteByIdSql;
        AddParameter(command, "@Id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            return;
        }

        var connection = await this.connectionFactory.CreateConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();

        var paramNames = new string[idList.Count];
        for (var i = 0; i < idList.Count; i++)
        {
            paramNames[i] = $"@p{i}";
            AddParameter(command, paramNames[i], idList[i].ToString("D"));
        }

        command.CommandText = string.Format(
            CultureInfo.InvariantCulture,
            this.dialect.DeadLetterDeleteByIdsSql,
            string.Join(", ", paramNames));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task EnsureTableExistsAsync(CancellationToken cancellationToken)
    {
        var connection = await this.connectionFactory.CreateConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = this.dialect.DeadLetterCreateTableSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DeadLetterEntry ReadEntry(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
        MessageType = reader.GetString(reader.GetOrdinal("MessageType")),
        Body = new BinaryData(GetBytes(reader, reader.GetOrdinal("Body"))),
        SourceEndpoint = reader.GetString(reader.GetOrdinal("SourceEndpoint")),
        ExceptionType = reader.IsDBNull(reader.GetOrdinal("ExceptionType")) ? null : reader.GetString(reader.GetOrdinal("ExceptionType")),
        ExceptionMessage = reader.IsDBNull(reader.GetOrdinal("ExceptionMessage")) ? null : reader.GetString(reader.GetOrdinal("ExceptionMessage")),
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
        AddParameter(command, "@MessageType", (object?)filter.MessageType ?? DBNull.Value);
        AddParameter(command, "@ExceptionType", (object?)filter.ExceptionType ?? DBNull.Value);
        AddParameter(command, "@Since", filter.Since.HasValue
            ? filter.Since.Value.ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);
        AddParameter(command, "@Until", filter.Until.HasValue
            ? filter.Until.Value.ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
