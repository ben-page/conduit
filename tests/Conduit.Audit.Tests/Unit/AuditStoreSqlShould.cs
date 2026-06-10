using AwesomeAssertions;
using Conduit.Audit.Persistence;
using Conduit.Persistence;
using Conduit.Persistence.MySql;
using Conduit.Persistence.Postgres;
using Conduit.Persistence.Sqlite;
using Conduit.Persistence.SqlServer;

namespace Conduit.Audit.Tests.Unit;

/// <summary>
/// Verifies the audit module composes its CREATE TABLE DDL correctly from each engine's shared
/// <see cref="ISqlDialect"/> type vocabulary. The audit dialects were folded into the shared
/// persistence layer in this stage, so these per-engine structural assertions replace the deleted
/// <c>AuditDialectsShould</c> tests. The composed insert / select / delete CRUD and the round-trip
/// behaviour are covered functionally against real SQLite by <c>LocalAuditStoreShould</c>; this class
/// covers the DDL composition for the engines that have no live database in unit tests.
/// </summary>
[TestClass]
public sealed class AuditStoreSqlShould
{
    // Every column the audit table must declare, regardless of engine.
    private static readonly string[] AuditColumns =
    [
        AuditTableSchema.IdColumn, AuditTableSchema.MessageIdColumn, AuditTableSchema.ConversationIdColumn,
        AuditTableSchema.CausationIdColumn, AuditTableSchema.MessageTypeColumn, AuditTableSchema.MessageKindColumn,
        AuditTableSchema.ServiceNameColumn, AuditTableSchema.PayloadColumn, AuditTableSchema.PropertiesColumn,
        AuditTableSchema.TimestampColumn, AuditTableSchema.ObservationsColumn,
    ];

    public static IEnumerable<object[]> AllDialects =>
    [
        [new SqliteDialect()],
        [new SqlServerDialect()],
        [new PostgresDialect()],
        [new MySqlDialect()],
    ];

    [TestMethod]
    [DynamicData(nameof(AllDialects))]
    public void ComposeCreateTableTargetingTheAuditTableWithEveryColumn(ISqlDialect dialect)
    {
        // Arrange / Act
        var sql = ComposeCreateTableSql(dialect);

        // Assert
        sql.Should().Contain(AuditTableSchema.TableName);
        foreach (var column in AuditColumns)
        {
            sql.Should().Contain(column, $"the store declares the '{column}' column");
        }
    }

    [TestMethod]
    [DynamicData(nameof(AllDialects))]
    public void ComposeCreateTableUsingTheEnginesTypeVocabulary(ISqlDialect dialect)
    {
        // Arrange / Act
        var sql = ComposeCreateTableSql(dialect);

        // Assert — the DDL must be built from the engine's own type names, not a hard-coded vocabulary.
        sql.Should().Contain(dialect.BigIntAutoIncrementPrimaryKey);
        sql.Should().Contain(dialect.TimestampType);
        sql.Should().Contain(dialect.TextType);
    }

    [TestMethod]
    public void ComposeSqliteCreateTableWithTextAndAutoincrementPrimaryKey()
    {
        // Arrange / Act
        var sql = ComposeCreateTableSql(new SqliteDialect());

        // Assert
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS conduit_audit");
        sql.Should().Contain("INTEGER PRIMARY KEY AUTOINCREMENT");
        sql.Should().Contain("TEXT");
    }

    [TestMethod]
    public void ComposeSqlServerCreateTableWithNvarcharMaxTimestampText()
    {
        // Arrange / Act
        var sql = ComposeCreateTableSql(new SqlServerDialect());

        // Assert — timestamps are stored as ISO 8601 text (read via GetString), so the Timestamp
        // column is NVARCHAR(MAX), not DATETIME2.
        sql.Should().Contain("Timestamp NVARCHAR(MAX) NOT NULL");
        sql.Should().NotContain("DATETIME2");
        sql.Should().Contain("BIGINT IDENTITY(1,1) PRIMARY KEY");
    }

    [TestMethod]
    public void ComposePostgresCreateTableWithTextTimestampAndBigserial()
    {
        // Arrange / Act
        var sql = ComposeCreateTableSql(new PostgresDialect());

        // Assert — Postgres folds the PascalCase identifiers to lower case transparently; timestamps
        // are TEXT (ISO 8601), not TIMESTAMPTZ, because the store reads them with GetString.
        sql.Should().Contain("Timestamp TEXT NOT NULL");
        sql.Should().NotContain("TIMESTAMPTZ");
        sql.Should().Contain("BIGSERIAL PRIMARY KEY");
    }

    [TestMethod]
    public void ComposeMySqlCreateTableWithLongTextTimestamp()
    {
        // Arrange / Act
        var sql = ComposeCreateTableSql(new MySqlDialect());

        // Assert — timestamps are LONGTEXT (ISO 8601), not DATETIME(6).
        sql.Should().Contain("Timestamp LONGTEXT NOT NULL");
        sql.Should().NotContain("DATETIME(6)");
        sql.Should().Contain("LONGTEXT");
        sql.Should().Contain("BIGINT AUTO_INCREMENT PRIMARY KEY");
    }

    // Builds the store against the engine dialect with a null connection factory — only the composed
    // CreateTableSql is read, so no connection is opened.
    private static string ComposeCreateTableSql(ISqlDialect dialect)
    {
        var store = new DbAuditStore(new UnusedConnectionFactory(), dialect);
        return store.CreateTableSql;
    }

    /// <summary>
    /// Connection factory that throws if asked for a connection. These tests only read the store's
    /// composed DDL string; they never execute SQL, so no real connection is ever needed.
    /// </summary>
    private sealed class UnusedConnectionFactory : IConduitConnectionFactory
    {
        public Task<System.Data.Common.DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("These tests inspect composed SQL only and open no connections.");
    }
}
