using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// Wraps an engine's connection factory so every connection it hands out first has
/// <c>SET TRANSACTION ISOLATION LEVEL</c> run on it, the way a host's earlier transaction leaves a pooled
/// session at a level other than the default. A claim that does not pin its own level then meets that
/// level exactly as it would in production.
/// </summary>
internal sealed class IsolationLevelSettingConnectionFactory : IConduitConnectionFactory
{
    private readonly IConduitConnectionFactory inner;
    private readonly string levelName;

    /// <summary>Wraps <paramref name="inner"/> so its connections carry <paramref name="level"/>.</summary>
    /// <param name="inner">The engine's factory.</param>
    /// <param name="level">The level to set; one of ReadUncommitted, ReadCommitted, RepeatableRead, Serializable, Snapshot.</param>
    public IsolationLevelSettingConnectionFactory(IConduitConnectionFactory inner, IsolationLevel level)
    {
        this.inner = inner;
        this.levelName = level switch
        {
            IsolationLevel.ReadUncommitted => "READ UNCOMMITTED",
            IsolationLevel.ReadCommitted => "READ COMMITTED",
            IsolationLevel.RepeatableRead => "REPEATABLE READ",
            IsolationLevel.Serializable => "SERIALIZABLE",
            IsolationLevel.Snapshot => "SNAPSHOT",
            IsolationLevel.Unspecified or IsolationLevel.Chaos => throw new ArgumentOutOfRangeException(nameof(level), level, "Not a SQL Server session level."),
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Not a SQL Server session level."),
        };
    }

    /// <inheritdoc/>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The level name comes from a fixed switch over an enum, never from user input.")]
    public async Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = await this.inner.CreateConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET TRANSACTION ISOLATION LEVEL {this.levelName}";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
