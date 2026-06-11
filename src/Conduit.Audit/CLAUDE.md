# Conduit.Audit

Operational audit trail for Conduit dispatch pipelines. Captures observations emitted by the generated mediator, consolidates them into per-dispatch records, and publishes them as `AuditBatch` events for local persistence or cross-service aggregation.

## Persistence engine (Conduit.Persistence)

Audit shares the relational-persistence abstraction in **`Conduit.Persistence`** with Durability. Audit depends on `Conduit.Persistence` only — it has **no** direct database-driver dependency (the `Microsoft.Data.Sqlite` PackageReference was removed). The engine is resolved from DI: `IConduitConnectionFactory` (connections) + `ISqlDialect` (engine type vocabulary). See `src/Conduit.Persistence/CLAUDE.md`.

- Register the engine on the **`ConduitBuilder`**, not the listener builder: `conduit.UseSqlite(connectionString)` (from `Conduit.Persistence.Sqlite`) — or `UsePostgres` / `UseSqlServer` / `UseMySql` — alongside `conduit.AddAuditListener(...)`. The engine is shared with Durability when both are configured (call `UseXxx` once).
- `DbAuditStore` extends `DbStoreBase`; `AuditTableInitializer` extends `TableInitializerBase`; `AuditPurgeService` extends `PeriodicCleanupServiceBase`.
- The audit dialects (`IAuditDialect` + `SqliteAuditDialect`/`SqlServerAuditDialect`/`PostgresAuditDialect`/`MySqlAuditDialect`), `IAuditConnectionFactory`, and `AuditConnectionStringFactory<T>` were **deleted** — their work moved into the shared `ISqlDialect`/`IConduitConnectionFactory` plus the module's own `AuditTableSchema` constants.

## Directory layout

- `Configuration/` — `AuditBuilder` (capture side), `AuditListenerBuilder` (persistence side), `AuditOptions`
- `Runtime/` — `AuditObserver`, `DispatchGroup`, `AuditBatchPublisher`, `AuditBatch`, `DispatchAuditRecord`, `LocalAuditListener`, `AuditPurgeService`, `AuditObserverInitializer`, `AuditTableInitializer`
- `Persistence/` — `IAuditStore`, `DbAuditStore`, `AuditTableSchema` (module-owned table + column identifiers)

## Key types

- `AuditObserver` — `IConduitObserver` implementation. Receives `MessageObservation` callbacks from `MessageContext.Observe()`. Groups observations by `MessageId` in a `ConcurrentDictionary<string, DispatchGroup>`. Seals the group on `DispatchCompleted`/`DispatchFailed` and writes the resulting `DispatchAuditRecord` to a bounded channel. Filters out `AuditBatch` type observations to prevent infinite recursion. Also honours per-type suppression: it takes two `IReadOnlySet<string>` of full type names — **suppressed types** (no group is ever created, so nothing is recorded) and **suppressed-payload types** (group is recorded but the body is nulled out via `DispatchGroup.SuppressPayload()`). `EvictStaleGroups(now, staleTimeout)` removes groups whose `CreatedAt` is older than `staleTimeout` relative to `now`; `now` is a parameter for deterministic testing. Evicted groups are sealed as failed (a synthetic `DispatchFailed` is appended via `DispatchGroup.MarkIncomplete`) and flushed to the channel rather than dropped, preserving the partial trail.
- `DispatchGroup` — internal type that accumulates observations for a single dispatch. Captures `MessageContext` snapshot (message ID, conversation ID, causation ID, properties, payload) at creation time. `Seal()` produces a `DispatchAuditRecord`. `SuppressPayload()` nulls the captured body (for `SuppressPayload<T>()` types). `MarkIncomplete(staleTimeout)` appends a synthetic `DispatchFailed` for stale-group eviction. `CreatedAt` is the eviction reference time.
- `AuditBatchPublisher` — `BackgroundService` that drains `DispatchAuditRecord` instances from the channel and publishes them as `AuditBatch` events via `IMediator.PublishAsync`. Creates a new DI scope per batch for correct handler lifetime. Batch size controlled by `AuditOptions.BatchSize`. Also runs a concurrent `PeriodicTimer` sweep loop that calls `AuditObserver.EvictStaleGroups` to evict incomplete dispatch groups (sweep cadence = `min(StaleGroupTimeout, 1 minute)`). This is the single owner of stale-group eviction.
- `AuditBatch` — `IEvent` record containing `SourceService`, `SourceHost`, `BatchTimestamp`, and `Records`. Marked `[DoNotAudit]` to prevent the audit infrastructure from auditing its own events.
- `DispatchAuditRecord` — the consolidated audit record for a single dispatch: message context fields, type info, raw JSON payload, properties, timestamp, and the full observation list.
- `LocalAuditListener` — `IEventListener<AuditBatch>` (transient) that persists records to `IAuditStore`. Registered by `AddAuditListener`, whether batches are published in-process or arrive over a transport. Resolved per-publish inside the dispatch scope, so it can consume the **Scoped** `IAuditStore`.
- `AuditPurgeService` — extends `PeriodicCleanupServiceBase`. Periodically deletes expired records based on `AuditOptions.RetentionPeriod`. The base class runs each tick inside a fresh `IServiceScopeFactory` scope and the purge resolves `IAuditStore` from that scoped provider in `CleanupAsync` — it does **not** inject the store into the constructor. This avoids a captive-dependency bug: the store is now Scoped, so a singleton `BackgroundService` injecting it directly would throw "Cannot consume scoped service from singleton" at startup under `ValidateScopes`.
- `AuditObserverInitializer` — `IHostedService` that wires `AuditObserver` to `MessageContext.Observer` at startup and clears it at shutdown.
- `AuditTableInitializer` — extends `TableInitializerBase`. Creates the audit table at startup (scoped) and **fails fast** in `ValidateConfiguration` if `AddAuditListener` was called but no persistence engine (`IConduitConnectionFactory`/`ISqlDialect`) is registered, pointing at `conduit.UseSqlite(...)`.

## Configuration

Two orthogonal entry points on `ConduitBuilder` — **`AddAudit`** captures, **`AddAuditListener`** persists. Compose them per deployment role.

**`AddAudit()`** — registers the capture pipeline (observer, channel, batch publisher) that emits `AuditBatch` events. Used by any service that wants its own dispatches audited:
```csharp
conduit.AddAudit(audit =>
{
    audit.ServiceName = "OrderService";
    audit.BatchSize = 50;
});
```

**`AddAuditListener()`** — registers the `IEventListener<AuditBatch>`, store, table initializer, and retention purge. The events it consumes may be published in-process (paired with `AddAudit`) or arrive over a transport from other services — it handles both identically. It owns retention (`RetentionPeriod`/`PurgeInterval`). The persistence engine is configured separately on the `ConduitBuilder` (`conduit.UseSqlite(...)`) and shared with Durability via DI — `AuditListenerBuilder` no longer has `UseSqlite/UsePostgres/UseSqlServer/UseMySql`:
```csharp
conduit.UseSqlite("Data Source=audit.db");          // engine on the ConduitBuilder, shared with durability
conduit.AddAuditListener(listener =>
{
    listener.RetentionPeriod = TimeSpan.FromDays(30);
});
```

Deployment roles:
- **Producer only** (ships audit to a collector): `AddAudit()` alone.
- **Single service, stores own audit**: `conduit.UseSqlite(...)` + `AddAudit()` + `AddAuditListener(...)`.
- **Central collector** (persists `AuditBatch` from many services): `conduit.UsePostgres(...)` + `AddAuditListener(...)` alone (no `AddAudit`).

`AuditBuilder` exposes public properties for capture tuning (`ServiceName`, `BatchSize`, `ChannelCapacity`, `BackpressureMode`, `StaleGroupTimeout`) plus `Suppress<T>()` (skip all recording for a type) and `SuppressPayload<T>()` (record observations but omit body). Both suppression methods are wired: `AddAudit` resolves their `Type` sets to full-type-name sets and passes them to `AuditObserver`. `StaleGroupTimeout` is wired into the `AuditBatchPublisher` sweep. `AuditListenerBuilder` exposes `RetentionPeriod` and `PurgeInterval`. The builders' properties delegate to an internal `AuditOptions`, which the `AddAudit`/`AddAuditListener` extensions copy into `IOptions<AuditOptions>`. Persistence is purely event-driven via `LocalAuditListener`, which is why local and distributed storage share one entry point.

**No metrics aggregation.** Audit captures the full observation trail; it does not compute metrics. The previously-inert `AuditOptions.FlushInterval` and `AuditOptions.MetricsAggregationInterval` knobs (and `AuditBuilder.FlushInterval`) were **removed** — there was no flush timer (the `AuditBatchPublisher` flushes only when `BatchSize` accumulates or the channel signals readable) and no aggregation feature. Real-time metrics come from the built-in OpenTelemetry `"Conduit"` `Meter` (`ConduitTelemetry`), not from audit.

## Observation flow

1. Generated mediator calls `MessageContext.Observe(observation)` at dispatch boundaries (`DispatchInvoked`, `DispatchCompleted`, `DispatchFailed`)
2. `MessageContext.Observer` (static, set by `AuditObserverInitializer`) forwards to `AuditObserver.OnObservation`
3. Observer groups observations by `MessageId` in `DispatchGroup`, seals on terminal observation
4. Sealed `DispatchAuditRecord` written to bounded `Channel<DispatchAuditRecord>`
5. `AuditBatchPublisher` drains channel, publishes `AuditBatch` event via mediator
6. `LocalAuditListener` (or any `IEventListener<AuditBatch>`) persists to store

## Recursion suppression

Recursion (auditing the `AuditBatch` publish itself) is prevented by `AuditObserver`, which explicitly skips `DispatchInvoked` observations where `MessageType` matches `AuditBatch`. `AuditBatch` is also marked `[DoNotAudit]`, but note that `[DoNotAudit]` only suppresses **payload capture** in the generated code (`MediatorEmitter` gates the `SetPayload` call on `!isDoNotAudit`) — `Observe()` calls are still emitted for `[DoNotAudit]` types. So the observer's type filter, not the attribute, is what actually breaks the recursion.

## Persistence

- `IAuditStore` — interface: `SaveBatchAsync`, `GetByMessageIdAsync`, `PurgeExpiredAsync`, `EnsureTableExistsAsync`. Registered **Scoped** by `AddAuditListener` (it depends on the Scoped `IConduitConnectionFactory`).
- `DbAuditStore` — extends `DbStoreBase`; depends on `IConduitConnectionFactory` + `ISqlDialect` (resolved from DI). Composes its CREATE TABLE DDL from the dialect's type-name helpers (`BigIntAutoIncrementPrimaryKey`, `TextType`/`NullableTextType`, `TimestampType`) and its own `AuditTableSchema` identifiers; the insert / select-by-message-id / delete-expired-purge SQL are module-owned constants (identical across engines, like durability's CRUD). Table `conduit_audit`; uniform PascalCase columns across all engines (Postgres folds unquoted identifiers to lower case transparently — no more snake_case aliasing).
- `AuditTableSchema` — module-owned `static` constants for the table + column names. Mirrors durability's `OutboxTableSchema`.
- **UTC timestamps via ISO 8601 text**: matching the durability stores, `DbAuditStore` writes timestamps as round-trippable UTC ISO 8601 text (`DateTime.ToString("O")`) and parses them back with `DateTimeStyles.RoundtripKind`, stamping `DateTimeKind.Utc`. This is engine-agnostic — it needs no per-engine `DbType` and no native-datetime binding, so the audit module did **not** require adding an audit-specific timestamp primitive to `ISqlDialect`. (The previous native-`DateTime`-binding approach with `DbType.DateTime2`/`DbType.Object` was the only thing genuinely divergent across audit's old dialects; switching to durability's text approach removed the divergence entirely.) Full tick precision is preserved by the `"O"` round-trip format.
- **No audit dialects.** The four audit dialect classes + `IAuditDialect` + `IAuditConnectionFactory` + `AuditConnectionStringFactory<T>` were deleted; the engine type vocabulary lives in the shared `ISqlDialect` (covered per-engine by the Durability project's `PostgresDialectShould`/`SqlServerDialectShould`/`MySqlDialectShould`/`SqliteDialectShould`). The audit module's composed CREATE TABLE DDL is asserted per-engine by `AuditStoreSqlShould` (no DB), and the full composed CRUD is covered functionally by `LocalAuditStoreShould` round-tripping against real SQLite. The Singleton→Scoped lifetime safety is proved by `AuditScopeValidationShould` (builds the host under `ValidateScopes`/`ValidateOnBuild` and exercises the purge + dispatch paths).

## Zero-cost when not opted in

When `AddAudit()` is not called, `MessageContext.Observer` remains null. The generated mediator's `Observe()` calls are guarded by a null check, making the audit infrastructure zero-cost when not configured.
