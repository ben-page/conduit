# Conduit.Persistence

Shared relational-persistence abstraction used by Conduit's storage modules (Durability and Audit). Engine-agnostic — targets `net10.0`, references Conduit core + the DI/Hosting abstractions. The concrete engine lives in a separate package (e.g. `Conduit.Persistence.Sqlite`) and is registered into core's `ConduitBuilder` via a `UseXxx` extension, mirroring how transport packages register via `UseTransport`.

## Abstraction surface

- **`IConduitConnectionFactory`** — `Task<DbConnection> CreateConnectionAsync(CancellationToken)` opens a fresh connection that the **caller owns and disposes**. `DbTransaction? GetCurrentTransaction()` has a default `null` implementation (audit never overrides it; durability's transaction-aware factories do, to surface the handler's ambient transaction for the outbox write path).
- **`INonTransactionalConnectionFactory`** — public marker. Built-in connection-string factories implement it; they can never enlist the handler's transaction. The durability outbox does an `is INonTransactionalConnectionFactory` fail-fast at startup. Public so consuming modules can run that check. (Marker interface; uses the same `#pragma warning disable CA1040` convention as core's `IEvent`.)
- **`ISqlDialect`** — one implementation per engine, exposing every idiom both modules use:
  - `DbType TimestampDbType` and `DbParameterHelper` for binding.
  - DDL type-name helpers (`BigIntAutoIncrementPrimaryKey`, `TimestampType`/`NullableTimestampType`, `TextType`/`NullableTextType`, `BlobType`/`NullableBlobType`, `IntegerType`, `SmallIntType`). Modules compose their own CREATE TABLE strings from these — **DDL templates stay in the module**, only the engine type vocabulary lives here.
  - `BuildInsertOrIgnoreSql(table, columns, primaryKeyColumn)` — insert-or-ignore (SQLite: `INSERT OR IGNORE`; Postgres: `ON CONFLICT (...) DO NOTHING`; SQL Server: `INSERT ... SELECT ... WHERE NOT EXISTS`; MySQL: `INSERT IGNORE`).
  - `PaginationSuffix` — ordered-SELECT page clause (SQLite/Postgres/MySQL: `LIMIT @PageSize OFFSET @Offset`; SQL Server: `OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY`).
  - `BuildExistsSql(table, whereColumn)` — single-row existence probe. SQLite/Postgres/MySQL cap with `LIMIT 1`; SQL Server omits the cap (no `LIMIT` syntax). Routed through the dialect because the inbox `Exists` query is engine-divergent.
  - `BuildClaimPendingSql(IClaimableTableSchema)` / `BuildClaimByIdsSql(IClaimableTableSchema)` — the **complete** atomic outbox-claim SQL with identifiers substituted from the schema. The claim logic (claim token, pending→claiming transition, stale-claim grace cutoff, stuck-row recovery) is preserved per-engine; only table/column identifiers are templated. **Status literals are bound parameters** (`@PendingStatus`/`@ClaimingStatus`) — the dialect carries no module-specific status knowledge; the durability outbox store supplies the actual `OutboxEntryStatus` enum values. Each engine uses its own concurrency primitive: SQLite none (single-writer), Postgres/MySQL `FOR UPDATE SKIP LOCKED` (MySQL wraps the row-selection in a derived table since it cannot reference the UPDATE target in a subquery), SQL Server `TOP (@BatchSize)` + `WITH (ROWLOCK, UPDLOCK, READPAST)`, with the batch opening `SET TRANSACTION ISOLATION LEVEL READ COMMITTED;` — `READPAST` refuses the Snapshot or Serializable level a pooled session may carry (SQL 650), and the SET reverts when the parameterized batch returns (`SqlServerClaimIsolationShould`).
- **`IClaimableTableSchema`** — table + column accessors the claim builders need (`TableName`, `IdColumn`, `MessageIdColumn`, `StatusColumn`, `CreatedAtColumn`, `ClaimedAtColumn`, `ClaimTokenColumn`).
- **`DbStoreBase`** — abstract base for ADO.NET stores. Provides the disposal-correct connection-per-method pattern (`await using var connection = await ConnectionFactory.CreateConnectionAsync(...)`) with `ExecuteNonQueryAsync` / `ExecuteScalarAsync` / `ExecuteReaderAsync<T>` helpers, plus a static `ExecuteOnConnectionAsync(connection, transaction, ...)` for the outbox `SaveAsync` path that writes through a **caller-supplied transaction's own connection** (which the store does not own). CA2100 is suppressed at the class level by `[SuppressMessage]` with justification (all SQL is trusted infrastructure) — the same attribute-based approach the stores used before extraction.
- **`DbParameterHelper`** — static `Add(command, name, value)` and `Add(command, name, value, DbType)`.
- **`TableInitializerBase : IHostedService`** — scoped-on-startup table creation. Overridable `ValidateConfiguration(IServiceProvider scope)` hook (fail-fast) and `CreateTablesAsync(IServiceProvider scope, ...)`.
- **`PeriodicCleanupServiceBase : BackgroundService`** — `abstract Interval` + `abstract CleanupAsync(IServiceProvider scope, ...)`; each tick runs in a fresh `IServiceScopeFactory` scope so a scoped store can be resolved safely. A tick that throws is logged and the next runs after `Interval` (while the host is stopping, any exception is the stop's cancellation and logs nothing — SqlClient reports one as a `SqlException`) — escaping, it ended the service and, under the default `StopHost`, the host. It logs under the derived type's category through `ILoggerFactory` resolved from the first tick's scope, so derived constructors take no logger.

## Engine-package pattern

An engine package references `Conduit.Persistence` + its ADO.NET provider and ships:
1. A `IConduitConnectionFactory` (+ `INonTransactionalConnectionFactory`) backed by a connection string.
2. An `ISqlDialect` implementation.
3. A `public static ConduitBuilder UseXxx(this ConduitBuilder builder, string connectionString)` extension that registers `IConduitConnectionFactory` (Scoped) + `ISqlDialect` (Singleton) into `builder.Services`. `ConduitBuilder.Services` is already public (same access transport extensions use), so no new accessor is needed.

Consuming modules resolve `IConduitConnectionFactory` + `ISqlDialect` from DI and never reference a specific engine. See `Conduit.Persistence.Sqlite` for the reference implementation.

## Engine packages

Four engine packages ship today, all implementing the **complete** `ISqlDialect` surface above:

| Package | Connection factory | Dialect | Extension | Provider |
|---|---|---|---|---|
| `Conduit.Persistence.Sqlite` | `SqliteConduitConnectionFactory` | `SqliteDialect` | `UseSqlite` | Microsoft.Data.Sqlite |
| `Conduit.Persistence.Postgres` | `PostgresConduitConnectionFactory` | `PostgresDialect` | `UsePostgres` | Npgsql |
| `Conduit.Persistence.SqlServer` | `SqlServerConduitConnectionFactory` | `SqlServerDialect` | `UseSqlServer` | Microsoft.Data.SqlClient |
| `Conduit.Persistence.MySql` | `MySqlConduitConnectionFactory` | `MySqlDialect` | `UseMySql` | MySqlConnector |

**Column-naming consistency:** all four dialects compose DDL using the consuming module's hard-coded **PascalCase** identifiers (the module's non-claim SQL constants reference `MessageId`, `Status`, `CreatedAt`, etc., and `OutboxTableSchema` exposes the same). PostgreSQL folds unquoted identifiers to lower case, so its DDL and the module's unquoted queries resolve to the same columns transparently; SQL Server / MySQL column names are case-insensitive. This keeps the four engines agreeing on the single set of column names the module owns.

Per-engine claim SQL is asserted byte-for-byte (no DB connection) by `PostgresDialectShould` / `SqlServerDialectShould` / `MySqlDialectShould` in the Durability test project. Container-backed integration tests for all three server engines live in `tests/Conduit.Persistence.IntegrationTests/` (real Postgres/SqlServer/MySql via Testcontainers), exercising the stores end-to-end.

## See also

- `src/Conduit.Persistence.Sqlite/` — SQLite engine (reference implementation): `SqliteConduitConnectionFactory`, `SqliteDialect`, `conduit.UseSqlite(connectionString)`.
- `src/Conduit.Persistence.Postgres/`, `src/Conduit.Persistence.SqlServer/`, `src/Conduit.Persistence.MySql/` — the other three engines.
- `src/Conduit.Durability/CLAUDE.md` — first consumer of this abstraction.
