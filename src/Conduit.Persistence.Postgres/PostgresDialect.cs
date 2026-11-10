using System.Data;
using System.Globalization;

namespace Conduit.Persistence.Postgres;

/// <summary>
/// PostgreSQL engine dialect. Concurrent claiming uses <c>FOR UPDATE SKIP LOCKED</c> on the inner
/// row-selection subquery. The persistence stores write timestamps as round-trippable ISO 8601 text
/// and read them back with <c>reader.GetString(...)</c>, so timestamp columns are <c>TEXT</c> and
/// <see cref="TimestampDbType"/> is <see cref="DbType.String"/>. A native <c>TIMESTAMPTZ</c> column
/// would reject the string-bound parameter (<c>42804</c>), break the text-vs-text claim comparison
/// (<c>42883</c>), and fail <c>GetString</c> on read.
/// </summary>
/// <remarks>
/// Column identifiers in the composed DDL match the consuming module's hard-coded PascalCase
/// identifiers (e.g. <c>MessageId</c>). PostgreSQL folds unquoted identifiers to lower case, so the
/// DDL and the module's unquoted queries resolve to the same lower-cased columns transparently.
/// </remarks>
public sealed class PostgresDialect : ISqlDialect
{
    /// <inheritdoc/>
    public DbType TimestampDbType => DbType.String;

    /// <inheritdoc/>
    public string BigIntAutoIncrementPrimaryKey => "BIGSERIAL PRIMARY KEY";

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
    public string BlobType => "BYTEA";

    /// <inheritdoc/>
    public string NullableBlobType => "BYTEA";

    /// <inheritdoc/>
    public string IntegerType => "INT";

    /// <inheritdoc/>
    public string SmallIntType => "SMALLINT";

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
            "INSERT INTO {0} ({1})\nVALUES ({2})\nON CONFLICT ({3}) DO NOTHING",
            table,
            columnList,
            parameterList,
            primaryKeyColumn);
    }

    /// <inheritdoc/>
    public string BuildClaimPendingSql(IClaimableTableSchema schema)
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
                  AND {6} <= @Now
                ORDER BY {1}
                LIMIT @BatchSize
                FOR UPDATE SKIP LOCKED
            )
            """,
            schema.TableName,
            schema.IdColumn,
            schema.StatusColumn,
            schema.CreatedAtColumn,
            schema.ClaimedAtColumn,
            schema.ClaimTokenColumn,
            schema.NextAttemptAtColumn);
    }

    /// <inheritdoc/>
    public string BuildClaimByIdsSql(IClaimableTableSchema schema)
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
                  AND {7} = 0
                ORDER BY {1}
                FOR UPDATE SKIP LOCKED
            )
            """,
            schema.TableName,
            schema.IdColumn,
            schema.MessageIdColumn,
            schema.StatusColumn,
            schema.CreatedAtColumn,
            schema.ClaimedAtColumn,
            schema.ClaimTokenColumn,
            schema.AttemptCountColumn);
    }
}
