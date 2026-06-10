using System.Globalization;
using Conduit.Persistence;

namespace Conduit.Durability.Persistence.Inbox;

/// <summary>
/// ADO.NET inbox store. SQL identifiers are module-owned; engine-specific idioms
/// (insert-or-ignore, type names) come from <see cref="ISqlDialect"/>.
/// </summary>
public sealed class DbConnectionInboxStore : DbStoreBase, IInboxStore
{
    private const string DeleteExpiredSql = "DELETE FROM conduit_inbox WHERE ProcessedAt < @Cutoff";

    private readonly string createTableSql;
    private readonly string insertSql;
    private readonly string existsSql;

    /// <summary>Initializes a new instance of <see cref="DbConnectionInboxStore"/>.</summary>
    public DbConnectionInboxStore(IConduitConnectionFactory connectionFactory, ISqlDialect dialect)
        : base(connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(dialect);

        this.createTableSql = dialect.BuildCreateTableIfNotExistsSql(
            "conduit_inbox",
            $"""
                MessageId {dialect.KeyTextType} PRIMARY KEY,
                ProcessedAt {dialect.TimestampType} NOT NULL
            """);
        this.insertSql = dialect.BuildInsertOrIgnoreSql(
            "conduit_inbox",
            ["MessageId", "ProcessedAt"],
            "MessageId");
        this.existsSql = dialect.BuildExistsSql("conduit_inbox", "MessageId");
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string messageId, CancellationToken cancellationToken)
    {
        var result = await this.ExecuteScalarAsync(
            this.existsSql,
            command => DbParameterHelper.Add(command, "@MessageId", messageId),
            cancellationToken);
        return result is not null;
    }

    /// <inheritdoc/>
    public async Task<bool> RecordAsync(string messageId, CancellationToken cancellationToken)
    {
        // Insert-or-ignore semantics mean rows-affected is 1 for a newly recorded message and
        // 0 when the row already existed. This is the atomic dedup decision — no separate read.
        var rowsAffected = await this.ExecuteNonQueryAsync(
            this.insertSql,
            command =>
            {
                DbParameterHelper.Add(command, "@MessageId", messageId);
                DbParameterHelper.Add(command, "@ProcessedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            },
            cancellationToken);
        return rowsAffected > 0;
    }

    /// <inheritdoc/>
    public Task DeleteExpiredAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(
            DeleteExpiredSql,
            command => DbParameterHelper.Add(
                command,
                "@Cutoff",
                (DateTime.UtcNow - retentionPeriod).ToString("O", CultureInfo.InvariantCulture)),
            cancellationToken);

    /// <inheritdoc/>
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken)
        => this.ExecuteNonQueryAsync(this.createTableSql, configureParameters: null, cancellationToken);
}
