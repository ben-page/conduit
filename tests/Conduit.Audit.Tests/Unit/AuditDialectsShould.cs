using System.Data;
using AwesomeAssertions;
using Conduit.Audit.Persistence;

namespace Conduit.Audit.Tests.Unit;

/// <summary>
/// Structural SQL-string tests for every <see cref="IAuditDialect"/> implementation.
/// Mirrors the durability project's per-dialect unit-test bar: no container integration,
/// just verification that each engine's SQL exposes the contract <see cref="DbAuditStore"/> depends on.
/// </summary>
[TestClass]
public class AuditDialectsShould
{
    // Column names the store reads by ordinal — every dialect's SELECT must expose them
    // (snake_case engines alias back to PascalCase).
    private static readonly string[] ProjectedColumns =
    [
        "MessageId", "ConversationId", "CausationId", "MessageType",
        "MessageKind", "ServiceName", "Payload", "Properties", "Timestamp", "Observations",
    ];

    // Parameter names are engine-independent (ADO.NET @-names), so every INSERT must contain them.
    private static readonly string[] InsertParameters =
    [
        "@MessageId", "@ConversationId", "@CausationId", "@MessageType",
        "@MessageKind", "@ServiceName", "@Payload", "@Properties", "@Timestamp", "@Observations",
    ];

    public static IEnumerable<object[]> AllDialects =>
    [
        [new SqliteAuditDialect()],
        [new SqlServerAuditDialect()],
        [new PostgresAuditDialect()],
        [new MySqlAuditDialect()],
    ];

    public static IEnumerable<object[]> RelationalDialects =>
    [
        [new SqliteAuditDialect()],
        [new SqlServerAuditDialect()],
        [new MySqlAuditDialect()],
    ];

    [TestMethod]
    [DynamicData(nameof(AllDialects))]
    public void CreateTableSqlTargetsTheAuditTable(IAuditDialect dialect)
    {
        dialect.CreateAuditTableSql.Should().Contain("conduit_audit");
    }

    [TestMethod]
    [DynamicData(nameof(AllDialects))]
    public void InsertSqlTargetsTheAuditTableWithAllParameters(IAuditDialect dialect)
    {
        // Arrange / Act
        var sql = dialect.InsertAuditRecordSql;

        // Assert
        sql.Should().Contain("INSERT INTO conduit_audit");
        foreach (var parameter in InsertParameters)
        {
            sql.Should().Contain(parameter);
        }
    }

    [TestMethod]
    [DynamicData(nameof(AllDialects))]
    public void SelectByMessageIdSqlExposesEveryProjectedColumn(IAuditDialect dialect)
    {
        // Arrange / Act
        var sql = dialect.SelectByMessageIdSql;

        // Assert
        sql.Should().Contain("SELECT");
        sql.Should().Contain("conduit_audit");
        sql.Should().Contain("@MessageId");
        foreach (var column in ProjectedColumns)
        {
            sql.Should().Contain(column, $"the store reads the '{column}' column by name");
        }
    }

    [TestMethod]
    [DynamicData(nameof(AllDialects))]
    public void DeleteExpiredSqlDeletesByCutoff(IAuditDialect dialect)
    {
        // Arrange / Act
        var sql = dialect.DeleteExpiredSql;

        // Assert
        sql.Should().Contain("DELETE");
        sql.Should().Contain("conduit_audit");
        sql.Should().Contain("@Cutoff");
    }

    // Timestamp parameter typing: relational engines take a native datetime via DateTime2,
    // Postgres lets Npgsql infer timestamptz from a Kind=Utc DateTime (DbType.Object).

    [TestMethod]
    [DynamicData(nameof(RelationalDialects))]
    public void RelationalEnginesBindTimestampsAsDateTime2(IAuditDialect dialect)
    {
        dialect.TimestampDbType.Should().Be(DbType.DateTime2);
    }

    [TestMethod]
    public void PostgresLetsNpgsqlInferTimestamptzFromUtcKind()
    {
        new PostgresAuditDialect().TimestampDbType.Should().Be(DbType.Object);
    }

    // Engine-specific schema expectations.

    [TestMethod]
    public void SqlServerUsesDateTime2AndIdempotentGuard()
    {
        // Arrange / Act
        var sql = new SqlServerAuditDialect().CreateAuditTableSql;

        // Assert
        sql.Should().Contain("IF NOT EXISTS");
        sql.Should().Contain("DATETIME2");
        sql.Should().Contain("NVARCHAR(MAX)");
    }

    [TestMethod]
    public void PostgresUsesTimestamptzAndAliasesColumns()
    {
        // Arrange / Act
        var dialect = new PostgresAuditDialect();

        // Assert
        dialect.CreateAuditTableSql.Should().Contain("CREATE TABLE IF NOT EXISTS");
        dialect.CreateAuditTableSql.Should().Contain("TIMESTAMPTZ");
        dialect.CreateAuditTableSql.Should().Contain("message_id");
        dialect.SelectByMessageIdSql.Should().Contain("AS \"MessageId\"");
    }

    [TestMethod]
    public void MySqlUsesMicrosecondDateTimeAndLongText()
    {
        // Arrange / Act
        var sql = new MySqlAuditDialect().CreateAuditTableSql;

        // Assert
        sql.Should().Contain("CREATE TABLE IF NOT EXISTS");
        sql.Should().Contain("DATETIME(6)");
        sql.Should().Contain("LONGTEXT");
    }
}
