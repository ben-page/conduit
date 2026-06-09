# Conduit.Audit

Operational audit trail for Conduit dispatch pipelines. Captures observations emitted by the generated mediator, consolidates them into per-dispatch records, and publishes them as `AuditBatch` events for local persistence or cross-service aggregation.

## Directory layout

- `Configuration/` — `AuditBuilder` (capture side), `AuditListenerBuilder` (persistence side), `AuditOptions`
- `Runtime/` — `AuditObserver`, `DispatchGroup`, `AuditBatchPublisher`, `AuditBatch`, `DispatchAuditRecord`, `LocalAuditListener`, `AuditPurgeService`, `AuditObserverInitializer`, `AuditTableInitializer`
- `Persistence/` — `IAuditStore`, `DbAuditStore`, `IAuditDialect` (+ `SqliteAuditDialect`, `SqlServerAuditDialect`, `PostgresAuditDialect`, `MySqlAuditDialect`), `IAuditConnectionFactory`, `AuditConnectionStringFactory<T>`

## Key types

- `AuditObserver` — `IConduitObserver` implementation. Receives `MessageObservation` callbacks from `MessageContext.Observe()`. Groups observations by `MessageId` in a `ConcurrentDictionary<string, DispatchGroup>`. Seals the group on `DispatchCompleted`/`DispatchFailed` and writes the resulting `DispatchAuditRecord` to a bounded channel. Filters out `AuditBatch` type observations to prevent infinite recursion.
- `DispatchGroup` — internal type that accumulates observations for a single dispatch. Captures `MessageContext` snapshot (message ID, conversation ID, causation ID, properties, payload) at creation time. `Seal()` produces a `DispatchAuditRecord`.
- `AuditBatchPublisher` — `BackgroundService` that drains `DispatchAuditRecord` instances from the channel and publishes them as `AuditBatch` events via `IMediator.PublishAsync`. Creates a new DI scope per batch for correct handler lifetime. Batch size controlled by `AuditOptions.BatchSize`.
- `AuditBatch` — `IEvent` record containing `SourceService`, `SourceHost`, `BatchTimestamp`, and `Records`. Marked `[DoNotAudit]` to prevent the audit infrastructure from auditing its own events.
- `DispatchAuditRecord` — the consolidated audit record for a single dispatch: message context fields, type info, raw JSON payload, properties, timestamp, and the full observation list.
- `LocalAuditListener` — `IEventListener<AuditBatch>` that persists records to `IAuditStore`. Registered by `AddAuditListener`, whether batches are published in-process or arrive over a transport.
- `AuditPurgeService` — `BackgroundService` that periodically deletes expired records based on `AuditOptions.RetentionPeriod`.
- `AuditObserverInitializer` — `IHostedService` that wires `AuditObserver` to `MessageContext.Observer` at startup and clears it at shutdown.
- `AuditTableInitializer` — `IHostedService` that calls `IAuditStore.EnsureTableExistsAsync` at startup.

## Configuration

Two orthogonal entry points on `ConduitBuilder` — **`AddAudit`** captures, **`AddAuditListener`** persists. Compose them per deployment role.

**`AddAudit()`** — registers the capture pipeline (observer, channel, batch publisher) that emits `AuditBatch` events. Used by any service that wants its own dispatches audited:
```csharp
conduit.AddAudit(audit =>
{
    audit.Options.ServiceName = "OrderService";
    audit.Options.BatchSize = 50;
});
```

**`AddAuditListener()`** — registers the `IEventListener<AuditBatch>`, store, table initializer, and retention purge. The events it consumes may be published in-process (paired with `AddAudit`) or arrive over a transport from other services — it handles both identically. It owns retention (`RetentionPeriod`/`PurgeInterval`):
```csharp
conduit.AddAuditListener(listener =>
{
    listener.UseSqlite("Data Source=audit.db");
});
```

Deployment roles:
- **Producer only** (ships audit to a collector): `AddAudit()` alone.
- **Single service, stores own audit**: `AddAudit()` + `AddAuditListener(l => l.UseSqlite(...))`.
- **Central collector** (persists `AuditBatch` from many services): `AddAuditListener(l => l.UsePostgres<NpgsqlConnection>(...))` alone.

`AuditBuilder` also exposes `Suppress<T>()` (skip all recording for a type) and `SuppressPayload<T>()` (record observations but omit body). Persistence is purely event-driven via `LocalAuditListener`, which is why local and distributed storage share one entry point.

## Observation flow

1. Generated mediator calls `MessageContext.Observe(observation)` at dispatch boundaries (`DispatchInvoked`, `DispatchCompleted`, `DispatchFailed`)
2. `MessageContext.Observer` (static, set by `AuditObserverInitializer`) forwards to `AuditObserver.OnObservation`
3. Observer groups observations by `MessageId` in `DispatchGroup`, seals on terminal observation
4. Sealed `DispatchAuditRecord` written to bounded `Channel<DispatchAuditRecord>`
5. `AuditBatchPublisher` drains channel, publishes `AuditBatch` event via mediator
6. `LocalAuditListener` (or any `IEventListener<AuditBatch>`) persists to store

## Recursion suppression

`AuditBatch` is marked `[DoNotAudit]`. The source generator checks this attribute and omits `Observe()` calls from the dispatch path for `[DoNotAudit]` types. Additionally, `AuditObserver` explicitly skips `DispatchInvoked` observations where `MessageType` matches `AuditBatch`, as a runtime safety net.

## Persistence

- `IAuditStore` — interface: `SaveBatchAsync`, `GetByMessageIdAsync`, `PurgeExpiredAsync`, `EnsureTableExistsAsync`
- `DbAuditStore` — ADO.NET implementation, delegates SQL to `IAuditDialect`. Database-agnostic.
- `IAuditDialect` — SQL generation interface. One implementation per database engine. Same engine coverage as `Conduit.Durability`: SQLite, SQL Server, PostgreSQL, MySQL. Table: `conduit_audit` with indexes on message id, conversation id, `(message type, timestamp)`, and timestamp.
  - `SqliteAuditDialect` (built-in, `Microsoft.Data.Sqlite`), `SqlServerAuditDialect`, `PostgresAuditDialect`, `MySqlAuditDialect`. Only SQLite is referenced by the project; the other engines use the generic `UseXxx<TConnection>(connectionString)` builder pattern (`TConnection : DbConnection`), so the consumer brings the provider package (Microsoft.Data.SqlClient / Npgsql / MySqlConnector). Postgres uses snake_case columns aliased back to the PascalCase names the store reads by ordinal.
- **UTC timestamps everywhere**: the store reads and writes UTC. `IAuditDialect.TimestampDbType` lets each engine pick its native datetime mapping — `DbType.DateTime2` for the relational engines (→ `DATETIME2` / `DATETIME(6)` / SQLite text), and `DbType.Object` for Postgres so Npgsql infers `timestamptz` from the `DateTimeKind.Utc` value (an explicit `DateTime2` would resolve to `timestamp without time zone` and throw). On read, `DbAuditStore` stamps the value `Utc` (`timestamptz` already returns Utc; the relational engines return Unspecified).
- `AuditListenerBuilder` is the single place persistence is wired (dialect + connection factory + store + `LocalAuditListener` + table initializer + purge); its private `Register<TDialect, TConnection>` keeps the four engine methods DRY. Dialect SQL is unit-tested at the string level in `AuditDialectsShould` (no container integration, matching the durability bar).

## Zero-cost when not opted in

When `AddAudit()` is not called, `MessageContext.Observer` remains null. The generated mediator's `Observe()` calls are guarded by a null check, making the audit infrastructure zero-cost when not configured.
