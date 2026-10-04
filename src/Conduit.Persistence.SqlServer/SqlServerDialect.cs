using System.Data;
using System.Globalization;

namespace Conduit.Persistence.SqlServer;

/// <summary>
/// SQL Server engine dialect. Concurrent claiming uses the <c>WITH (ROWLOCK, UPDLOCK, READPAST)</c>
/// table hint plus <c>TOP (@BatchSize)</c> on the inner row-selection subquery. The persistence
/// stores write timestamps as round-trippable ISO 8601 text and read them back with
/// <c>reader.GetString(...)</c>, so timestamp columns are <c>NVARCHAR(MAX)</c> and
/// <see cref="TimestampDbType"/> is <see cref="DbType.String"/>. A native <c>DATETIME2</c> column
/// accepts the ISO string on insert but materializes a <see cref="DateTime"/> that
/// <c>GetString</c> cannot cast on read.
/// </summary>
/// <remarks>
/// SQL Server has no <c>LIMIT</c> clause: pagination uses <c>OFFSET ... ROWS FETCH NEXT ... ROWS
/// ONLY</c>, the existence probe omits a row cap, and insert-or-ignore uses an
/// <c>INSERT ... WHERE NOT EXISTS</c> guard. Identifiers in the composed DDL match the consuming
/// module's PascalCase identifiers (SQL Server identifiers are case-insensitive by default).
/// </remarks>
public sealed class SqlServerDialect : ISqlDialect
{
    /// <inheritdoc/>
    public DbType TimestampDbType => DbType.String;

    /// <inheritdoc/>
    public string BigIntAutoIncrementPrimaryKey => "BIGINT IDENTITY(1,1) PRIMARY KEY";

    /// <inheritdoc/>
    public string TimestampType => "NVARCHAR(MAX)";

    /// <inheritdoc/>
    public string NullableTimestampType => "NVARCHAR(MAX)";

    /// <inheritdoc/>
    public string TextType => "NVARCHAR(MAX)";

    /// <inheritdoc/>
    public string NullableTextType => "NVARCHAR(MAX)";

    /// <inheritdoc/>
    public string KeyTextType => "NVARCHAR(450)";

    /// <inheritdoc/>
    public string BlobType => "VARBINARY(MAX)";

    /// <inheritdoc/>
    public string NullableBlobType => "VARBINARY(MAX)";

    /// <inheritdoc/>
    public string IntegerType => "INT";

    /// <inheritdoc/>
    public string SmallIntType => "TINYINT";

    /// <inheritdoc/>
    public string PaginationSuffix => "OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

    /// <inheritdoc/>
    public string BuildCreateTableIfNotExistsSql(string tableName, string columnDefinitions)
    {
        // SQL Server has no CREATE TABLE IF NOT EXISTS; guard the bare CREATE with an OBJECT_ID check.
        return string.Format(
            CultureInfo.InvariantCulture,
            "IF OBJECT_ID(N'{0}', N'U') IS NULL\nCREATE TABLE {0} (\n{1}\n)",
            tableName,
            columnDefinitions);
    }

    /// <inheritdoc/>
    public string BuildExistsSql(string table, string whereColumn)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "SELECT 1 FROM {0} WHERE {1} = @{1}",
            table,
            whereColumn);
    }

    /// <inheritdoc/>
    public string BuildInsertOrIgnoreSql(string table, string[] columns, string primaryKeyColumn)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var columnList = string.Join(", ", columns);
        var parameterList = string.Join(", ", columns.Select(column => "@" + column));

        // UPDLOCK + HOLDLOCK take a serializable key-range lock on the existence probe so concurrent
        // callers serialize: the first inserts, the rest see the row and skip. Without these hints the
        // WHERE NOT EXISTS guard is a TOCTOU race and concurrent inserts violate the primary key.
        return string.Format(
            CultureInfo.InvariantCulture,
            "INSERT INTO {0} ({1})\nSELECT {2}\nWHERE NOT EXISTS (SELECT 1 FROM {0} WITH (UPDLOCK, HOLDLOCK) WHERE {3} = @{3})",
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
                SELECT TOP (@BatchSize) {1} FROM {0} WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE ({2} = @PendingStatus OR ({2} = @ClaimingStatus AND {4} <= @ClaimCutoff))
                  AND {3} <= @GraceCutoff
                ORDER BY {1}
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
    public string BuildClaimByIdsSql(IClaimableTableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return string.Format(
            CultureInfo.InvariantCulture,
            """
            UPDATE {0}
            SET {3} = @ClaimingStatus, {5} = @ClaimedAt, {6} = @ClaimToken
            WHERE {1} IN (
                SELECT {1} FROM {0} WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE {2} IN ({{0}})
                  AND ({3} = @PendingStatus OR ({3} = @ClaimingStatus AND {5} <= @ClaimCutoff))
                  AND {7} = 0
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
