using System.Data;
using System.Globalization;

namespace Conduit.Persistence.MySql;

/// <summary>
/// MySQL 8.0+ engine dialect. Concurrent claiming uses <c>FOR UPDATE SKIP LOCKED</c>. MySQL forbids
/// referencing the UPDATE target table directly in a subquery, so the inner row-selection is wrapped
/// in a derived table (<c>... AS claimable</c>). The persistence stores write timestamps as
/// round-trippable ISO 8601 text and read them back with <c>reader.GetString(...)</c>, so timestamp
/// columns are <c>LONGTEXT</c> and <see cref="TimestampDbType"/> is <see cref="DbType.String"/>. A
/// native <c>DATETIME(6)</c> column rejects the ISO string (the <c>Z</c> suffix) on insert.
/// </summary>
/// <remarks>
/// Identifiers in the composed DDL match the consuming module's PascalCase identifiers. MySQL
/// identifier case-sensitivity depends on the server's <c>lower_case_table_names</c> setting for
/// table names; column names are case-insensitive, so the module's queries resolve regardless.
/// </remarks>
public sealed class MySqlDialect : ISqlDialect
{
    /// <inheritdoc/>
    public DbType TimestampDbType => DbType.String;

    /// <inheritdoc/>
    public string BigIntAutoIncrementPrimaryKey => "BIGINT AUTO_INCREMENT PRIMARY KEY";

    /// <inheritdoc/>
    public string TimestampType => "LONGTEXT";

    /// <inheritdoc/>
    public string NullableTimestampType => "LONGTEXT";

    /// <inheritdoc/>
    public string TextType => "LONGTEXT";

    /// <inheritdoc/>
    public string NullableTextType => "LONGTEXT";

    /// <inheritdoc/>
    public string KeyTextType => "VARCHAR(255)";

    /// <inheritdoc/>
    public string BlobType => "LONGBLOB";

    /// <inheritdoc/>
    public string NullableBlobType => "LONGBLOB";

    /// <inheritdoc/>
    public string IntegerType => "INT";

    /// <inheritdoc/>
    public string SmallIntType => "TINYINT";

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
            "INSERT IGNORE INTO {0} ({1})\nVALUES ({2})",
            table,
            columnList,
            parameterList);
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
                SELECT {1} FROM (
                    SELECT {1} FROM {0}
                    WHERE ({2} = @PendingStatus OR ({2} = @ClaimingStatus AND {4} <= @ClaimCutoff))
                      AND {3} <= @GraceCutoff
                      AND {6} <= @Now
                    ORDER BY {1}
                    LIMIT @BatchSize
                    FOR UPDATE SKIP LOCKED
                ) AS claimable
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
                SELECT {1} FROM (
                    SELECT {1} FROM {0}
                    WHERE {2} IN ({{0}})
                      AND ({3} = @PendingStatus OR ({3} = @ClaimingStatus AND {5} <= @ClaimCutoff))
                      AND {7} = 0
                    ORDER BY {1}
                    FOR UPDATE SKIP LOCKED
                ) AS claimable
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
