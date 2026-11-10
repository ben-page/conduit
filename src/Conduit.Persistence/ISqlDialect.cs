using System.Data;
using System.Data.Common;

namespace Conduit.Persistence;

/// <summary>
/// A single engine interface exposing the database idioms shared by Conduit's persistence modules
/// (durability and audit). One implementation per database engine (e.g. SQLite). DDL templates
/// (CREATE TABLE) and the non-claim CRUD statements live in the consuming module — composed from the
/// type-name helpers and builders here — so identifiers stay module-owned while engine-specific
/// syntax stays in the dialect.
/// </summary>
public interface ISqlDialect
{
    // Parameter binding --------------------------------------------------------------------------

    /// <summary>
    /// The <see cref="DbType"/> used for timestamp parameters. Engines that store timestamps as ISO
    /// 8601 text (SQLite) use <see cref="DbType.String"/>; engines with native timestamp types use
    /// <see cref="DbType.DateTime"/> or similar.
    /// </summary>
    public DbType TimestampDbType { get; }

    // DDL type-name helpers ----------------------------------------------------------------------
    // Sufficient to compose the outbox, inbox, and dead-letter-queue CREATE TABLE statements.

    /// <summary>Column definition for a 64-bit auto-incrementing primary key (e.g. SQLite <c>INTEGER PRIMARY KEY AUTOINCREMENT</c>).</summary>
    public string BigIntAutoIncrementPrimaryKey { get; }

    /// <summary>Type name for a non-null timestamp column.</summary>
    public string TimestampType { get; }

    /// <summary>Type name for a nullable timestamp column.</summary>
    public string NullableTimestampType { get; }

    /// <summary>Type name for a non-null text column.</summary>
    public string TextType { get; }

    /// <summary>Type name for a nullable text column.</summary>
    public string NullableTextType { get; }

    /// <summary>
    /// Type name for a text column that participates in a primary key or index (nullable unless the column
    /// adds <c>NOT NULL</c>). Engines with unbounded large-text types (SQL Server <c>NVARCHAR(MAX)</c>, MySQL
    /// <c>LONGTEXT</c>) cannot index them, so this is a bounded variant (e.g. <c>NVARCHAR(450)</c>,
    /// <c>VARCHAR(255)</c>) sized for identifier values (message ids, GUIDs) and for the ISO 8601 text the
    /// stores write timestamps as, when a timestamp is indexed (<see cref="TimestampType"/> is the general
    /// text type on those engines). Engines whose general text type is already indexable (SQLite/PostgreSQL
    /// <c>TEXT</c>) reuse it.
    /// </summary>
    public string KeyTextType { get; }

    /// <summary>Type name for a non-null binary/blob column.</summary>
    public string BlobType { get; }

    /// <summary>Type name for a nullable binary/blob column.</summary>
    public string NullableBlobType { get; }

    /// <summary>Type name for a 32-bit integer column.</summary>
    public string IntegerType { get; }

    /// <summary>Type name for a small integer column (used for status enums).</summary>
    public string SmallIntType { get; }

    // DDL composition ----------------------------------------------------------------------------

    /// <summary>
    /// Builds an idempotent "create table if it does not already exist" statement. SQLite, PostgreSQL,
    /// and MySQL support the inline <c>CREATE TABLE IF NOT EXISTS</c> form; SQL Server has no such
    /// clause, so it guards the bare <c>CREATE TABLE</c> with an <c>IF OBJECT_ID(...) IS NULL</c> check.
    /// </summary>
    /// <param name="tableName">The table to create.</param>
    /// <param name="columnDefinitions">
    /// The parenthesized column-definition body, without the surrounding <c>CREATE TABLE name (...)</c>
    /// — just the comma-separated column/constraint definitions.
    /// </param>
    /// <returns>The complete create-if-absent statement.</returns>
    public string BuildCreateTableIfNotExistsSql(string tableName, string columnDefinitions);

    /// <summary>
    /// Builds the statements that create a table and its indexes, each only if it does not already exist, to be
    /// run in order. SQLite and PostgreSQL follow the table with one <c>CREATE INDEX IF NOT EXISTS</c> per index;
    /// SQL Server, which has no such clause, guards each <c>CREATE INDEX</c> with a <c>sys.indexes</c> check. All
    /// three therefore add a missing index to an existing table. MySQL has no <c>IF NOT EXISTS</c> for an index either,
    /// and cannot guard a statement outside a stored program, so it declares the indexes inside its one
    /// <c>CREATE TABLE IF NOT EXISTS</c>: a table that already exists is left as it is.
    /// </summary>
    /// <param name="tableName">The table to create.</param>
    /// <param name="columnDefinitions">
    /// The comma-separated column/constraint definitions, as for
    /// <see cref="BuildCreateTableIfNotExistsSql(string, string)"/>.
    /// </param>
    /// <param name="indexes">The table's indexes; none gives the create-table statement alone.</param>
    /// <returns>The statements, in the order they must run.</returns>
    public IReadOnlyList<string> BuildCreateTableIfNotExistsSql(
        string tableName,
        string columnDefinitions,
        IReadOnlyList<TableIndex> indexes);

    /// <summary>
    /// Whether <paramref name="exception"/> is the engine's refusal to create a table or an index because one of that
    /// name already exists. A create-if-absent statement checks and then creates, so two replicas starting on an empty
    /// database can both find the object absent and both create it; the one that loses gets this error, and the object
    /// it was creating exists. SQL Server reports 2714 for a table and 1913 for an index; PostgreSQL reports 42P07 or
    /// 42710, or a unique violation (23505) in its own catalog (<c>pg_catalog</c>) when the two creates overlap; MySQL
    /// reports 1050 for a table and 1061 for an index. SQLite never does: it runs one writer at a time, so its
    /// <c>IF NOT EXISTS</c> is atomic. <see cref="DbStoreBase"/>'s <c>EnsureCreatedAsync</c> passes over a statement
    /// that fails so and runs the next.
    /// </summary>
    /// <param name="exception">The exception a create-if-absent statement threw.</param>
    /// <returns><see langword="true"/> when the object the statement was creating exists.</returns>
    public bool IsAlreadyExists(DbException exception);

    // Query idiom helpers ------------------------------------------------------------------------

    /// <summary>
    /// Builds <paramref name="statement"/> to run at READ COMMITTED whatever isolation level the session carries, for an
    /// update that concurrent callers race on one row and that must wait for the winner and re-check its <c>WHERE</c>
    /// rather than fail. SQL Server prefixes <c>SET TRANSACTION ISOLATION LEVEL READ COMMITTED;</c>: a pooled session
    /// keeps the level its last transaction set, and at Snapshot the second of two updates of one row fails with 3960.
    /// SQLite, PostgreSQL and MySQL return the statement as it is (no session level to carry, or a driver that resets it).
    /// </summary>
    /// <param name="statement">The statement, parameterized or not.</param>
    /// <returns>The statement to execute.</returns>
    public string BuildReadCommittedSql(string statement);

    /// <summary>
    /// Builds an insert-or-ignore statement (no error and no row written on a primary-key conflict).
    /// SQLite emits <c>INSERT OR IGNORE INTO ...</c>.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="columns">The columns to insert; each becomes an <c>@</c>-prefixed parameter.</param>
    /// <param name="primaryKeyColumn">The conflict-target primary key column.</param>
    /// <returns>The complete insert-or-ignore SQL.</returns>
    public string BuildInsertOrIgnoreSql(string table, string[] columns, string primaryKeyColumn);

    /// <summary>
    /// The pagination suffix appended to an ordered SELECT. SQLite/Postgres/MySQL emit
    /// <c>LIMIT @PageSize OFFSET @Offset</c>; SQL Server emits
    /// <c>OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY</c>.
    /// </summary>
    public string PaginationSuffix { get; }

    /// <summary>
    /// Builds an existence-probe statement that returns a single non-null marker row when at least
    /// one row matches <paramref name="whereColumn"/> = <c>@</c><paramref name="whereColumn"/>.
    /// Engines that support a row limit cap the probe (SQLite/Postgres/MySQL append
    /// <c>LIMIT 1</c>); SQL Server omits the limit because <c>LIMIT</c> is unsupported syntax there.
    /// </summary>
    /// <param name="table">The table to probe.</param>
    /// <param name="whereColumn">The column to match; also the bound parameter name (minus the <c>@</c>).</param>
    /// <returns>The complete existence-probe SQL.</returns>
    public string BuildExistsSql(string table, string whereColumn);

    // Outbox claim builders ----------------------------------------------------------------------

    /// <summary>
    /// Builds the complete atomic "claim pending" SQL: transition rows from the pending status
    /// (<c>@PendingStatus</c>) — or stale claiming rows (<c>@ClaimingStatus</c>) whose claimed-at is
    /// at or before <c>@ClaimCutoff</c> — to the claiming status (<c>@ClaimingStatus</c>), stamping
    /// <c>@ClaimedAt</c> and a per-worker <c>@ClaimToken</c>. Only rows with created-at at or before
    /// <c>@GraceCutoff</c> and next-attempt-at (<see cref="IClaimableTableSchema.NextAttemptAtColumn"/>) at or
    /// before <c>@Now</c> are eligible, limited to <c>@BatchSize</c>. The claimed rows are read back
    /// separately using the same <c>@ClaimToken</c>. The status values are bound parameters supplied
    /// by the caller, so the dialect carries no module-specific status knowledge.
    /// Parameters: <c>@ClaimToken</c>, <c>@ClaimedAt</c>, <c>@GraceCutoff</c>, <c>@Now</c>, <c>@ClaimCutoff</c>,
    /// <c>@BatchSize</c>, <c>@PendingStatus</c>, <c>@ClaimingStatus</c>. Identifiers are substituted
    /// from <paramref name="schema"/>.
    /// </summary>
    public string BuildClaimPendingSql(IClaimableTableSchema schema);

    /// <summary>
    /// Builds the complete atomic "claim by message ids" SQL: transition rows matching a MessageId
    /// list from the pending status (<c>@PendingStatus</c>) — or stale claiming rows
    /// (<c>@ClaimingStatus</c>) whose claimed-at is at or before <c>@ClaimCutoff</c> — to the claiming
    /// status (<c>@ClaimingStatus</c>), stamping <c>@ClaimedAt</c> and <c>@ClaimToken</c>. Only rows whose
    /// <see cref="IClaimableTableSchema.AttemptCountColumn"/> is zero are eligible: a by-id claim is a first
    /// attempt, and a row already attempted belongs to the claim-pending path's retries. The result
    /// contains a <c>{0}</c> placeholder where the caller substitutes the comma-separated
    /// <c>@p0, @p1, ...</c> parameter list. The status values are bound parameters. Parameters:
    /// <c>@ClaimToken</c>, <c>@ClaimedAt</c>, <c>@ClaimCutoff</c>, <c>@PendingStatus</c>,
    /// <c>@ClaimingStatus</c>, and the <c>@p0, @p1, ...</c> MessageId list. Identifiers are
    /// substituted from <paramref name="schema"/>.
    /// </summary>
    public string BuildClaimByIdsSql(IClaimableTableSchema schema);
}
