using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Conduit.Audit.Runtime;
using Conduit.Runtime;

namespace Conduit.Audit.Persistence;

/// <summary>
/// ADO.NET audit store implementation. Delegates all SQL to <see cref="IAuditDialect"/>,
/// making it compatible with any relational database that provides a <see cref="DbConnection"/>.
/// </summary>
// CA2100: All SQL in this class comes from IAuditDialect — a trusted internal interface, never user input.
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "All SQL originates from IAuditDialect, which is internal infrastructure, never user-supplied input.")]
public sealed class DbAuditStore : IAuditStore
{
    private readonly IAuditConnectionFactory connectionFactory;
    private readonly IAuditDialect dialect;

    /// <summary>
    /// Initializes a new instance of <see cref="DbAuditStore"/>.
    /// </summary>
    public DbAuditStore(IAuditConnectionFactory connectionFactory, IAuditDialect dialect)
    {
        this.connectionFactory = connectionFactory;
        this.dialect = dialect;
    }

    /// <inheritdoc/>
    public async Task SaveBatchAsync(IReadOnlyList<DispatchAuditRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return;
        }

        using var connection = this.connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        foreach (var record in records)
        {
            using var command = connection.CreateCommand();
            command.CommandText = this.dialect.InsertAuditRecordSql;

            AddParameter(command, "@MessageId", record.MessageId);
            AddParameter(command, "@ConversationId", record.ConversationId);
            AddParameter(command, "@CausationId", (object?)record.CausationId ?? DBNull.Value);
            AddParameter(command, "@MessageType", record.MessageType);
            AddParameter(command, "@MessageKind", record.MessageKind);
            AddParameter(command, "@ServiceName", record.ServiceName);
            AddParameter(command, "@Payload", record.Payload.HasValue
                ? record.Payload.Value.GetRawText()
                : DBNull.Value);
            AddParameter(command, "@Properties", record.Properties is not null
                ? JsonSerializer.Serialize(record.Properties, ConduitRuntime.JsonOptions)
                : DBNull.Value);
            this.AddTimestampParameter(command, "@Timestamp", record.Timestamp);
            AddParameter(command, "@Observations",
                JsonSerializer.Serialize(record.Observations, ConduitRuntime.JsonOptions));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task<DispatchAuditRecord?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageId);

        using var connection = this.connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = this.dialect.SelectByMessageIdSql;
        AddParameter(command, "@MessageId", messageId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    /// <inheritdoc/>
    public async Task PurgeExpiredAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        using var connection = this.connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = this.dialect.DeleteExpiredSql;
        this.AddTimestampParameter(command, "@Cutoff", cutoff);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task EnsureTableExistsAsync(CancellationToken cancellationToken)
    {
        using var connection = this.connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = this.dialect.CreateAuditTableSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DispatchAuditRecord ReadRecord(DbDataReader reader)
    {
        var causationIdOrdinal = reader.GetOrdinal("CausationId");
        var causationId = reader.IsDBNull(causationIdOrdinal) ? null : reader.GetString(causationIdOrdinal);

        var payloadOrdinal = reader.GetOrdinal("Payload");
        JsonElement? payload = reader.IsDBNull(payloadOrdinal)
            ? null
            : JsonSerializer.Deserialize<JsonElement>(reader.GetString(payloadOrdinal), ConduitRuntime.JsonOptions);

        var propertiesOrdinal = reader.GetOrdinal("Properties");
        IReadOnlyDictionary<string, object?>? properties = reader.IsDBNull(propertiesOrdinal)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(
                reader.GetString(propertiesOrdinal), ConduitRuntime.JsonOptions);

        var observationsJson = reader.GetString(reader.GetOrdinal("Observations"));
        var observations = JsonSerializer.Deserialize<List<MessageObservation>>(
            observationsJson, ConduitRuntime.JsonOptions) ?? [];

        // All engines store UTC; the value is read back as a native datetime and stamped Utc.
        // (timestamptz on Postgres already returns Utc; the relational engines return Unspecified.)
        var timestamp = DateTime.SpecifyKind(
            reader.GetDateTime(reader.GetOrdinal("Timestamp")), DateTimeKind.Utc);

        return new DispatchAuditRecord
        {
            MessageId = reader.GetString(reader.GetOrdinal("MessageId")),
            ConversationId = reader.GetString(reader.GetOrdinal("ConversationId")),
            CausationId = causationId,
            MessageType = reader.GetString(reader.GetOrdinal("MessageType")),
            MessageKind = reader.GetString(reader.GetOrdinal("MessageKind")),
            ServiceName = reader.GetString(reader.GetOrdinal("ServiceName")),
            Payload = payload,
            Properties = properties,
            Timestamp = timestamp,
            Observations = observations,
        };
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Binds a UTC timestamp parameter. Always normalizes the value to UTC, then applies the
    /// dialect's <see cref="IAuditDialect.TimestampDbType"/> so each engine maps it to its native
    /// datetime type (relational engines use <see cref="DbType.DateTime2"/>; Postgres uses
    /// <see cref="DbType.Object"/> so Npgsql infers <c>timestamptz</c> from the <see cref="DateTimeKind.Utc"/> value).
    /// </summary>
    private void AddTimestampParameter(DbCommand command, string name, DateTime value)
    {
        // Local values are converted; Utc and Unspecified are treated as already-UTC (the record contract).
        var utc = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = this.dialect.TimestampDbType;
        parameter.Value = utc;
        command.Parameters.Add(parameter);
    }
}
