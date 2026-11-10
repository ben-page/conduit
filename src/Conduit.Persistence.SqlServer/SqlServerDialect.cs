using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Conduit.Persistence.SqlServer;

/// <summary>
/// SQL Server engine dialect. Concurrent claiming pins <c>SET TRANSACTION ISOLATION LEVEL READ COMMITTED</c>
/// beside the <c>WITH (ROWLOCK, UPDLOCK, READPAST)</c> table hint, plus <c>TOP (@BatchSize)</c> on the inner row-selection subquery. The persistence
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

    // Every create runs in a transaction holding one exclusive application lock (database-scoped, released at commit),
    // so replicas starting together on an empty database create one at a time, each finding what the one before it
    // made: two concurrent CREATE INDEX on one table otherwise deadlock (1205), each holding the table's shared lock and
    // waiting for the schema-modification lock. It runs at READ COMMITTED, as the claims do: a pooled session can carry
    // Snapshot from a host's own transaction, and DDL at Snapshot that meets another session's DDL on the same table
    // fails with 3961 (metadata is not versioned). A create racing a process that takes no such lock still fails with
    // 2714 or 1913, which IsAlreadyExists recognizes. XACT_ABORT rolls the transaction back when the client times out
    // or cancels, rather than leaving it open on a pooled connection, and a lock not granted (a negative return) ends
    // the batch with an error instead of creating without it.
    private const string OneCreateAtATime =
        "SET XACT_ABORT ON;\nSET TRANSACTION ISOLATION LEVEL READ COMMITTED;\nBEGIN TRANSACTION;\n" +
        "DECLARE @lock int;\n" +
        "EXEC @lock = sp_getapplock @Resource = N'conduit_create_if_absent', @LockMode = N'Exclusive', @LockOwner = N'Transaction';\n" +
        "IF @lock < 0 THROW 51000, N'Conduit could not take its create lock.', 1;\n";

    private const string Commit = ";\nCOMMIT TRANSACTION;";

    /// <inheritdoc/>
    public string BuildCreateTableIfNotExistsSql(string tableName, string columnDefinitions)
    {
        // SQL Server has no CREATE TABLE IF NOT EXISTS; guard the bare CREATE with an OBJECT_ID check.
        return string.Format(
            CultureInfo.InvariantCulture,
            OneCreateAtATime + "IF OBJECT_ID(N'{0}', N'U') IS NULL\nCREATE TABLE {0} (\n{1}\n)" + Commit,
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

        // SQL Server has no CREATE INDEX IF NOT EXISTS either; guard each with a sys.indexes check.
        return
        [
            this.BuildCreateTableIfNotExistsSql(tableName, columnDefinitions),
            .. indexes.Select(index => string.Format(
                CultureInfo.InvariantCulture,
                OneCreateAtATime +
                "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{0}' AND object_id = OBJECT_ID(N'{1}'))\n" +
                "CREATE INDEX {0} ON {1} ({2})" +
                Commit,
                index.Name,
                tableName,
                string.Join(", ", index.Columns))),
        ];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The <c>OBJECT_ID</c> and <c>sys.indexes</c> guards are checks, not locks: Conduit's own creates take an application
    /// lock first, but a create racing a process that does not (a migration, an earlier build) can pass the check with it,
    /// and then fails with 2714 (an object of that name exists) for a table or 1913 for an index.
    /// </remarks>
    public bool IsAlreadyExists(DbException exception)
        => exception is SqlException { Number: 2714 or 1913 };

    /// <inheritdoc/>
    /// <remarks>
    /// A parameterized command reaches the server through <c>sp_executesql</c>, so the SET holds for the statement and
    /// reverts when it returns, leaving the session's carried level in place, as for the claims.
    /// </remarks>
    public string BuildReadCommittedSql(string statement)
        => "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;\n" + statement;

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
    // The claim sets READ COMMITTED itself, ahead of the UPDATE, for three reasons:
    //  - READPAST is accepted only under READ COMMITTED or REPEATABLE READ; any other level raises SQL error 650.
    //  - A pooled session keeps the level its last transaction set (measured on SQL Server 2022 with
    //    Microsoft.Data.SqlClient 7.1.1: SqlServerPooledSessionShould), and a host's own Snapshot
    //    transactions leave one behind.
    //  - After a claim the session still reads the level it carried (SqlServerClaimIsolationShould), so the
    //    connection goes back to the pool at that level. The likely reason (inferred, not measured): SqlClient
    //    sends a parameterized command through sp_executesql, and a SET inside it reverts on return.
    public string BuildClaimPendingSql(IClaimableTableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return string.Format(
            CultureInfo.InvariantCulture,
            """
            SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
            UPDATE {0}
            SET {2} = @ClaimingStatus, {4} = @ClaimedAt, {5} = @ClaimToken
            WHERE {1} IN (
                SELECT TOP (@BatchSize) {1} FROM {0} WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE ({2} = @PendingStatus OR ({2} = @ClaimingStatus AND {4} <= @ClaimCutoff))
                  AND {3} <= @GraceCutoff
                  AND {6} <= @Now
                ORDER BY {1}
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
    // The claim sets READ COMMITTED itself, ahead of the UPDATE, for three reasons:
    //  - READPAST is accepted only under READ COMMITTED or REPEATABLE READ; any other level raises SQL error 650.
    //  - A pooled session keeps the level its last transaction set (measured on SQL Server 2022 with
    //    Microsoft.Data.SqlClient 7.1.1: SqlServerPooledSessionShould), and a host's own Snapshot
    //    transactions leave one behind.
    //  - After a claim the session still reads the level it carried (SqlServerClaimIsolationShould), so the
    //    connection goes back to the pool at that level. The likely reason (inferred, not measured): SqlClient
    //    sends a parameterized command through sp_executesql, and a SET inside it reverts on return.
    public string BuildClaimByIdsSql(IClaimableTableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return string.Format(
            CultureInfo.InvariantCulture,
            """
            SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
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
