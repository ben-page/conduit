using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Conduit.Audit.Runtime;
using Conduit.Persistence;
using Conduit.Runtime;

namespace Conduit.Audit.Persistence;

/// <summary>
/// ADO.NET audit store. Extends <see cref="DbStoreBase"/> for the disposal-correct
/// connection-per-method pattern (a batch is one connection and one transaction), resolving the engine from DI via
/// <see cref="IConduitConnectionFactory"/> + <see cref="ISqlDialect"/>. SQL identifiers are
/// module-owned (see <see cref="AuditTableSchema"/>); engine-specific idioms (DDL type names) come
/// from the dialect. Timestamps are written as round-trippable ISO 8601 text and parsed back on read,
/// matching the durability stores — engine-agnostic, no native-datetime binding required.
/// </summary>
public sealed class DbAuditStore : DbStoreBase, IAuditStore
{
    private const string CausationIdColumnReader = AuditTableSchema.CausationIdColumn;

    private readonly ISqlDialect dialect;
    private readonly string insertSql;
    private readonly string selectByMessageIdSql;
    private readonly string deleteExpiredSql;

    /// <summary>Initializes a new instance of <see cref="DbAuditStore"/>.</summary>
    /// <param name="connectionFactory">The connection factory used to open per-method connections.</param>
    /// <param name="dialect">The engine dialect supplying DDL type names.</param>
    public DbAuditStore(IConduitConnectionFactory connectionFactory, ISqlDialect dialect)
        : base(connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(dialect);

        this.dialect = dialect;
        this.CreateTableSql = dialect.BuildCreateTableIfNotExistsSql(
            AuditTableSchema.TableName,
            $"""
                {AuditTableSchema.IdColumn} {dialect.BigIntAutoIncrementPrimaryKey},
                {AuditTableSchema.MessageIdColumn} {dialect.TextType} NOT NULL,
                {AuditTableSchema.ConversationIdColumn} {dialect.TextType} NOT NULL,
                {AuditTableSchema.CausationIdColumn} {dialect.NullableTextType},
                {AuditTableSchema.MessageTypeColumn} {dialect.TextType} NOT NULL,
                {AuditTableSchema.MessageKindColumn} {dialect.TextType} NOT NULL,
                {AuditTableSchema.ServiceNameColumn} {dialect.TextType} NOT NULL,
                {AuditTableSchema.PayloadColumn} {dialect.NullableTextType},
                {AuditTableSchema.PropertiesColumn} {dialect.NullableTextType},
                {AuditTableSchema.TimestampColumn} {dialect.TimestampType} NOT NULL,
                {AuditTableSchema.ObservationsColumn} {dialect.TextType} NOT NULL
            """);

        this.insertSql = $"""
            INSERT INTO {AuditTableSchema.TableName} ({AuditTableSchema.MessageIdColumn}, {AuditTableSchema.ConversationIdColumn}, {AuditTableSchema.CausationIdColumn}, {AuditTableSchema.MessageTypeColumn}, {AuditTableSchema.MessageKindColumn}, {AuditTableSchema.ServiceNameColumn}, {AuditTableSchema.PayloadColumn}, {AuditTableSchema.PropertiesColumn}, {AuditTableSchema.TimestampColumn}, {AuditTableSchema.ObservationsColumn})
            VALUES (@MessageId, @ConversationId, @CausationId, @MessageType, @MessageKind, @ServiceName, @Payload, @Properties, @Timestamp, @Observations)
            """;

        this.selectByMessageIdSql = $"""
            SELECT {AuditTableSchema.MessageIdColumn}, {AuditTableSchema.ConversationIdColumn}, {AuditTableSchema.CausationIdColumn}, {AuditTableSchema.MessageTypeColumn}, {AuditTableSchema.MessageKindColumn}, {AuditTableSchema.ServiceNameColumn}, {AuditTableSchema.PayloadColumn}, {AuditTableSchema.PropertiesColumn}, {AuditTableSchema.TimestampColumn}, {AuditTableSchema.ObservationsColumn}
            FROM {AuditTableSchema.TableName}
            WHERE {AuditTableSchema.MessageIdColumn} = @MessageId
            ORDER BY {AuditTableSchema.IdColumn} DESC
            """;

        this.deleteExpiredSql =
            $"DELETE FROM {AuditTableSchema.TableName} WHERE {AuditTableSchema.TimestampColumn} < @Cutoff";
    }

    /// <summary>
    /// The composed CREATE TABLE SQL for this store's engine. Exposed for tests to assert the audit
    /// DDL is built correctly from the engine dialect's type vocabulary (the functional round-trip
    /// against SQLite covers the rest of the composed CRUD).
    /// </summary>
    internal string CreateTableSql { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// The batch is written on one connection, in one transaction, committed after its last record: a record that fails
    /// rolls back those written before it (the uncommitted transaction rolls back as it is disposed) and the failure is
    /// thrown, so a batch the publisher logs as dropped has left nothing behind.
    /// </remarks>
    public async Task SaveBatchAsync(IReadOnlyList<DispatchAuditRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return;
        }

        await using var connection = await this.ConnectionFactory.CreateConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var record in records)
        {
            await ExecuteOnConnectionAsync(
                connection,
                transaction,
                this.insertSql,
                command =>
                {
                    DbParameterHelper.Add(command, "@MessageId", record.MessageId);
                    DbParameterHelper.Add(command, "@ConversationId", record.ConversationId);
                    DbParameterHelper.Add(command, "@CausationId", (object?)record.CausationId ?? DBNull.Value);
                    DbParameterHelper.Add(command, "@MessageType", record.MessageType);
                    DbParameterHelper.Add(command, "@MessageKind", record.MessageKind);
                    DbParameterHelper.Add(command, "@ServiceName", record.ServiceName);
                    DbParameterHelper.Add(command, "@Payload", record.Payload.HasValue
                        ? record.Payload.Value.GetRawText()
                        : DBNull.Value);
                    DbParameterHelper.Add(command, "@Properties", record.Properties is not null
                        ? JsonSerializer.Serialize(record.Properties, ConduitRuntime.JsonOptions)
                        : DBNull.Value);
                    DbParameterHelper.Add(command, "@Timestamp", FormatTimestamp(record.Timestamp));
                    DbParameterHelper.Add(command, "@Observations",
                        JsonSerializer.Serialize(record.Observations, ConduitRuntime.JsonOptions));
                },
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Several records can share a message ID: a dispatch silent past the stale timeout has a provisional record and
    /// then its real one, and a central store keeps each service's record of one logical message. The one returned is
    /// the most recently stored (highest <c>Id</c>), so a dispatch's real record wins over its provisional one.
    /// </remarks>
    public async Task<DispatchAuditRecord?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageId);

        var records = await this.ExecuteReaderAsync(
            this.selectByMessageIdSql,
            command => DbParameterHelper.Add(command, "@MessageId", messageId),
            ReadRecord,
            cancellationToken);

        return records.Count > 0 ? records[0] : null;
    }

    /// <inheritdoc/>
    public Task PurgeExpiredAsync(DateTime cutoff, CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(
            this.deleteExpiredSql,
            command => DbParameterHelper.Add(command, "@Cutoff", FormatTimestamp(cutoff)),
            cancellationToken);

    /// <inheritdoc/>
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken)
        => this.EnsureCreatedAsync(this.dialect, [this.CreateTableSql], cancellationToken);

    /// <summary>
    /// Formats a timestamp as round-trippable UTC ISO 8601 text. Local values are converted to UTC;
    /// Utc and Unspecified are treated as already-UTC (the <see cref="DispatchAuditRecord"/> contract).
    /// </summary>
    private static string FormatTimestamp(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return utc.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DispatchAuditRecord ReadRecord(DbDataReader reader)
    {
        var causationIdOrdinal = reader.GetOrdinal(CausationIdColumnReader);
        var causationId = reader.IsDBNull(causationIdOrdinal) ? null : reader.GetString(causationIdOrdinal);

        var payloadOrdinal = reader.GetOrdinal(AuditTableSchema.PayloadColumn);
        JsonElement? payload = reader.IsDBNull(payloadOrdinal)
            ? null
            : JsonSerializer.Deserialize<JsonElement>(reader.GetString(payloadOrdinal), ConduitRuntime.JsonOptions);

        var propertiesOrdinal = reader.GetOrdinal(AuditTableSchema.PropertiesColumn);
        IReadOnlyDictionary<string, object?>? properties = reader.IsDBNull(propertiesOrdinal)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(
                reader.GetString(propertiesOrdinal), ConduitRuntime.JsonOptions);

        var observationsJson = reader.GetString(reader.GetOrdinal(AuditTableSchema.ObservationsColumn));
        var observations = JsonSerializer.Deserialize<List<MessageObservation>>(
            observationsJson, ConduitRuntime.JsonOptions) ?? [];

        // Timestamps are stored as round-trippable UTC ISO 8601 text; parse back as UTC.
        var timestamp = DateTime.Parse(
            reader.GetString(reader.GetOrdinal(AuditTableSchema.TimestampColumn)),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        return new DispatchAuditRecord
        {
            MessageId = reader.GetString(reader.GetOrdinal(AuditTableSchema.MessageIdColumn)),
            ConversationId = reader.GetString(reader.GetOrdinal(AuditTableSchema.ConversationIdColumn)),
            CausationId = causationId,
            MessageType = reader.GetString(reader.GetOrdinal(AuditTableSchema.MessageTypeColumn)),
            MessageKind = reader.GetString(reader.GetOrdinal(AuditTableSchema.MessageKindColumn)),
            ServiceName = reader.GetString(reader.GetOrdinal(AuditTableSchema.ServiceNameColumn)),
            Payload = payload,
            Properties = properties,
            Timestamp = timestamp,
            Observations = observations,
        };
    }
}
