using System.Data;
using System.Globalization;

namespace Conduit.Persistence.Sqlite;

/// <summary>
/// SQLite engine dialect. SQLite is single-writer, so the claim builders need no row-locking hints.
/// Timestamps are stored and bound as ISO 8601 text, so <see cref="TimestampDbType"/> is
/// <see cref="DbType.String"/>.
/// </summary>
public sealed class SqliteDialect : ISqlDialect
{
    /// <inheritdoc/>
    public DbType TimestampDbType => DbType.String;

    /// <inheritdoc/>
    public string BigIntAutoIncrementPrimaryKey => "INTEGER PRIMARY KEY AUTOINCREMENT";

    /// <inheritdoc/>
    public string TimestampType => "TEXT";

    /// <inheritdoc/>
    public string NullableTimestampType => "TEXT";

    /// <inheritdoc/>
    public string TextType => "TEXT";

    /// <inheritdoc/>
    public string NullableTextType => "TEXT";

    /// <inheritdoc/>
    public string KeyTextType => "TEXT";

    /// <inheritdoc/>
    public string BlobType => "BLOB";

    /// <inheritdoc/>
    public string NullableBlobType => "BLOB";

    /// <inheritdoc/>
    public string IntegerType => "INTEGER";

    /// <inheritdoc/>
    public string SmallIntType => "INTEGER";

    /// <inheritdoc/>
    public string PaginationSuffix => "LIMIT @PageSize OFFSET @Offset";

    /// <inheritdoc/>
    public string BuildCreateTableIfNotExistsSql(string tableName, string columnDefinitions)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "CREATE TABLE IF NOT EXISTS {0} (\n{1}\n)",
            tableName,
            columnDefinitions);
    }

    /// <inheritdoc/>
    public string BuildExistsSql(string table, string whereColumn)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "SELECT 1 FROM {0} WHERE {1} = @{1} LIMIT 1",
            table,
            whereColumn);
    }

    /// <inheritdoc/>
    public string BuildInsertOrIgnoreSql(string table, string[] columns, string primaryKeyColumn)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var columnList = string.Join(", ", columns);
        var parameterList = string.Join(", ", columns.Select(column => "@" + column));

        return string.Format(
            CultureInfo.InvariantCulture,
            "INSERT OR IGNORE INTO {0} ({1})\nVALUES ({2})",
            table,
            columnList,
            parameterList);
    }

    /// <inheritdoc/>
    public string BuildClaimPendingSql(IOutboxTableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return string.Format(
            CultureInfo.InvariantCulture,
            """
            UPDATE {0}
            SET {2} = @ClaimingStatus, {4} = @ClaimedAt, {5} = @ClaimToken
            WHERE {1} IN (
                SELECT {1} FROM {0}
                WHERE ({2} = @PendingStatus OR ({2} = @ClaimingStatus AND {4} <= @ClaimCutoff))
                  AND {3} <= @GraceCutoff
                ORDER BY {1}
                LIMIT @BatchSize
            )
            """,
            schema.TableName,
            schema.IdColumn,
            schema.StatusColumn,
            schema.CreatedAtColumn,
            schema.ClaimedAtColumn,
            schema.ClaimTokenColumn);
    }

    /// <inheritdoc/>
    public string BuildClaimByIdsSql(IOutboxTableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return string.Format(
            CultureInfo.InvariantCulture,
            """
            UPDATE {0}
            SET {3} = @ClaimingStatus, {5} = @ClaimedAt, {6} = @ClaimToken
            WHERE {1} IN (
                SELECT {1} FROM {0}
                WHERE {2} IN ({{0}})
                  AND ({3} = @PendingStatus OR ({3} = @ClaimingStatus AND {5} <= @ClaimCutoff))
                ORDER BY {1}
            )
            """,
            schema.TableName,
            schema.IdColumn,
            schema.MessageIdColumn,
            schema.StatusColumn,
            schema.CreatedAtColumn,
            schema.ClaimedAtColumn,
            schema.ClaimTokenColumn);
    }
}
