using System.Data;
using System.Data.Common;
using System.Globalization;
using Npgsql;

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
    public IReadOnlyList<string> BuildCreateTableIfNotExistsSql(
        string tableName,
        string columnDefinitions,
        IReadOnlyList<TableIndex> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);

        return
        [
            this.BuildCreateTableIfNotExistsSql(tableName, columnDefinitions),
            .. indexes.Select(index => string.Format(
                CultureInfo.InvariantCulture,
                "CREATE INDEX IF NOT EXISTS {0} ON {1} ({2})",
                index.Name,
                tableName,
                string.Join(", ", index.Columns))),
        ];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 42P07 (<c>duplicate_table</c>) covers tables and indexes alike, both being relations, and 42710
    /// (<c>duplicate_object</c>) the row type a table brings with it. Two overlapping <c>CREATE ... IF NOT EXISTS</c>
    /// statements both pass the check, and the second then fails with one of those or on the catalog's own unique index
    /// (<c>pg_class_relname_nsp_index</c>, <c>pg_type_typname_nsp_index</c>) once the first commits: a unique violation
    /// in <c>pg_catalog</c> is only ever that, while one in an application table is a data error.
    /// </remarks>
    public bool IsAlreadyExists(DbException exception)
        => exception is PostgresException { SqlState: PostgresErrorCodes.DuplicateTable or PostgresErrorCodes.DuplicateObject }
            or PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, SchemaName: "pg_catalog" };

    /// <inheritdoc/>
    public string BuildReadCommittedSql(string statement) => statement;

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
