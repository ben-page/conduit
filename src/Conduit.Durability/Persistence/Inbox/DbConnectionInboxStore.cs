using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Conduit.Durability.Persistence.Dialects;

namespace Conduit.Durability.Persistence.Inbox;

/// <summary>
/// ADO.NET-based inbox store using dialect-specific SQL.
/// </summary>
// CA2100: All SQL in this class comes from IDurabilityDialect — a trusted internal interface, never user input.
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "All SQL originates from IDurabilityDialect, which is internal infrastructure, never user-supplied input.")]
public sealed class DbConnectionInboxStore(
    IDurabilityConnectionFactory connectionFactory,
    IDurabilityDialect dialect) : IInboxStore
{
    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string messageId, CancellationToken cancellationToken)
    {
        var connection = await connectionFactory.CreateConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = dialect.InboxExistsSql;
        AddParameter(command, "@MessageId", messageId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    /// <inheritdoc/>
    public async Task RecordAsync(string messageId, CancellationToken cancellationToken)
    {
        var connection = await connectionFactory.CreateConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = dialect.InboxInsertSql;
        AddParameter(command, "@MessageId", messageId);
        AddParameter(command, "@ProcessedAt", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DeleteExpiredAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
    {
        var connection = await connectionFactory.CreateConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = dialect.InboxDeleteExpiredSql;
        AddParameter(command, "@Cutoff", (DateTime.UtcNow - retentionPeriod).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task EnsureTableExistsAsync(CancellationToken cancellationToken)
    {
        var connection = await connectionFactory.CreateConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = dialect.InboxCreateTableSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
