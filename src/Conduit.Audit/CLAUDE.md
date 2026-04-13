# Conduit.Audit

Operational audit trail for Conduit dispatch pipelines. Captures observations emitted by the generated mediator, consolidates them into per-dispatch records, and publishes them as `AuditBatch` events for local persistence or cross-service aggregation.

## Directory layout

- `Configuration/` — `AuditBuilder`, `AuditConsumerBuilder`, `LocalStoreBuilder`, `AuditOptions`
- `Runtime/` — `AuditObserver`, `DispatchGroup`, `AuditBatchPublisher`, `AuditBatch`, `DispatchAuditRecord`, `LocalAuditListener`, `AuditPurgeService`, `AuditObserverInitializer`, `AuditTableInitializer`
- `Persistence/` — `IAuditStore`, `DbAuditStore`, `IAuditDialect`, `SqliteAuditDialect`, `IAuditConnectionFactory`, `AuditConnectionStringFactory<T>`

## Key types

- `AuditObserver` — `IConduitObserver` implementation. Receives `MessageObservation` callbacks from `MessageContext.Observe()`. Groups observations by `MessageId` in a `ConcurrentDictionary<string, DispatchGroup>`. Seals the group on `DispatchCompleted`/`DispatchFailed` and writes the resulting `DispatchAuditRecord` to a bounded channel. Filters out `AuditBatch` type observations to prevent infinite recursion.
- `DispatchGroup` — internal type that accumulates observations for a single dispatch. Captures `MessageContext` snapshot (message ID, conversation ID, causation ID, properties, payload) at creation time. `Seal()` produces a `DispatchAuditRecord`.
- `AuditBatchPublisher` — `BackgroundService` that drains `DispatchAuditRecord` instances from the channel and publishes them as `AuditBatch` events via `IMediator.PublishAsync`. Creates a new DI scope per batch for correct handler lifetime. Batch size controlled by `AuditOptions.BatchSize`.
- `AuditBatch` — `IEvent` record containing `SourceService`, `SourceHost`, `BatchTimestamp`, and `Records`. Marked `[DoNotAudit]` to prevent the audit infrastructure from auditing its own events.
- `DispatchAuditRecord` — the consolidated audit record for a single dispatch: message context fields, type info, raw JSON payload, properties, timestamp, and the full observation list.
- `LocalAuditListener` — `IEventListener<AuditBatch>` that persists records to `IAuditStore`. Registered by `UseLocalStore` or `AddAuditConsumer`.
- `AuditPurgeService` — `BackgroundService` that periodically deletes expired records based on `AuditOptions.RetentionPeriod`.
- `AuditObserverInitializer` — `IHostedService` that wires `AuditObserver` to `MessageContext.Observer` at startup and clears it at shutdown.
- `AuditTableInitializer` — `IHostedService` that calls `IAuditStore.EnsureTableExistsAsync` at startup.

## Configuration

Two entry points on `ConduitBuilder`:

**`AddAudit()`** — registers the observation pipeline (observer, channel, batch publisher). Used by every service that wants audit capture:
```csharp
conduit.AddAudit(audit =>
{
    audit.Options.ServiceName = "OrderService";
    audit.Options.BatchSize = 50;
    audit.UseLocalStore(store => store.UseSqlite("Data Source=audit.db"));
});
```

**`AddAuditConsumer()`** — registers just the consumer side (listener, store, purge). Used by a dedicated audit aggregation service that receives `AuditBatch` events via transport:
```csharp
conduit.AddAuditConsumer(consumer =>
{
    consumer.UseSqlite("Data Source=central-audit.db");
});
```

`AuditBuilder` also exposes `Suppress<T>()` (skip all recording for a type) and `SuppressPayload<T>()` (record observations but omit body).

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
- `IAuditDialect` — SQL generation interface. One implementation per database engine.
- `SqliteAuditDialect` — built-in SQLite dialect. Table: `conduit_audit` with indexes on `MessageId`, `ConversationId`, `(MessageType, Timestamp)`, and `Timestamp`.

## Zero-cost when not opted in

When `AddAudit()` is not called, `MessageContext.Observer` remains null. The generated mediator's `Observe()` calls are guarded by a null check, making the audit infrastructure zero-cost when not configured.
