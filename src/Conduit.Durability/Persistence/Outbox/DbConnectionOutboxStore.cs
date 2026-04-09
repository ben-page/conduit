using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Conduit.Durability.Persistence.Dialects;

namespace Conduit.Durability.Persistence.Outbox;

/// <summary>
/// ADO.NET outbox store implementation. Delegates all SQL to <see cref="IDurabilityDialect"/>,
/// making it compatible with any relational database that provides a <see cref="DbConnection"/>.
/// </summary>
// CA2100: All SQL in this class comes from IDurabilityDialect — a trusted internal interface, never user input.
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "All SQL originates from IDurabilityDialect, which is internal infrastructure, never user-supplied input.")]
public sealed class DbConnectionOutboxStore : IOutboxStore
{
    private readonly IDurabilityConnectionFactory connectionFactory;
    private readonly IDurabilityDialect dialect;

    /// <summary>
    /// Initializes a new instance of <see cref="DbConnectionOutboxStore"/>.
    /// </summary>
    public DbConnectionOutboxStore(IDurabilityConnectionFactory connectionFactory, IDurabilityDialect dialect)
    {
        this.connectionFactory = connectionFactory;
        this.dialect = dialect;
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
            using var command = connection.CreateCommand();
            command.CommandText = this.dialect.OutboxInsertEntrySql;
            command.Transaction = transaction;
            AddParameter(command, "@MessageId", entry.MessageId);
            AddParameter(command, "@ConversationId", entry.ConversationId);
            AddParameter(command, "@CausationId", (object?)entry.CausationId ?? DBNull.Value);
            AddParameter(command, "@Type", entry.Type);
            AddParameter(command, "@Payload", entry.Payload);
            AddParameter(command, "@CreatedAt", entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(
        int batchSize,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        var connection = await this.connectionFactory.CreateConnectionAsync(cancellationToken);
        var graceCutoff = DateTime.UtcNow.Subtract(gracePeriod).ToString("O", CultureInfo.InvariantCulture);

        using var command = connection.CreateCommand();
        command.CommandText = this.dialect.OutboxClaimPendingEntriesSql;
        AddParameter(command, "@GraceCutoff", graceCutoff);
        AddParameter(command, "@BatchSize", batchSize);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<OutboxEntry>();

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEntry(reader));
        }

        return results;
    }

    /// <inheritdoc/>
    public async Task MarkSentAsync(long entryId, CancellationToken cancellationToken)
    {
        var connection = await this.connectionFactory.CreateConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = this.dialect.OutboxMarkSentSql;
        AddParameter(command, "@Id", entryId);
        AddParameter(command, "@SentAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task IncrementRetryAsync(long entryId, CancellationToken cancellationToken)
    {
        var connection = await this.connectionFactory.CreateConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = this.dialect.OutboxIncrementRetrySql;
        AddParameter(command, "@Id", entryId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task MarkFailedAsync(long entryId, CancellationToken cancellationToken)
    {
        var connection = await this.connectionFactory.CreateConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = this.dialect.OutboxMarkFailedSql;
        AddParameter(command, "@Id", entryId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DeleteCompletedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
    {
        var connection = await this.connectionFactory.CreateConnectionAsync(cancellationToken);
        var retentionCutoff = DateTime.UtcNow.Subtract(retentionPeriod).ToString("O", CultureInfo.InvariantCulture);

        using var command = connection.CreateCommand();
        command.CommandText = this.dialect.OutboxDeleteCompletedSql;
        AddParameter(command, "@RetentionCutoff", retentionCutoff);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task EnsureTableExistsAsync(CancellationToken cancellationToken)
    {
        var connection = await this.connectionFactory.CreateConnectionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = this.dialect.OutboxCreateTableSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

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

        var connection = await this.connectionFactory.CreateConnectionAsync(cancellationToken);

        var paramNames = new string[messageIds.Count];
        for (var i = 0; i < messageIds.Count; i++)
        {
            paramNames[i] = $"@p{i}";
        }

        using var command = connection.CreateCommand();
        command.CommandText = string.Format(
            CultureInfo.InvariantCulture,
            this.dialect.OutboxClaimByMessageIdsSql,
            string.Join(", ", paramNames));

        for (var i = 0; i < messageIds.Count; i++)
        {
            AddParameter(command, $"@p{i}", messageIds[i]);
        }

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<ClaimedOutboxEntry>();

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ClaimedOutboxEntry
            {
                Id = reader.GetInt64(reader.GetOrdinal("Id")),
                MessageId = reader.GetString(reader.GetOrdinal("MessageId")),
            });
        }

        return results;
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
            Status = (OutboxEntryStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            CreatedAt = DateTime.Parse(
                reader.GetString(reader.GetOrdinal("CreatedAt")),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            SentAt = sentAt,
            RetryCount = reader.GetInt32(reader.GetOrdinal("RetryCount")),
        };
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
