# Conduit.Durability

Durable messaging infrastructure — inbox (inbound deduplication) and outbox (outbound capture + relay). Configured via `conduit.AddDurability()` with one shared database for all features.

## Directory layout

- `Configuration/` — `DurabilityBuilder`, `OutboxOptions`, `InboxOptions`
- `Persistence/` — `IDurabilityConnectionFactory`, `ConnectionStringFactory<T>` (internal), `IDurabilityDialect`
- `Persistence/Dialects/` — one class per database implementing `IDurabilityDialect` (SQLite, SQL Server, PostgreSQL, MySQL)
- `Persistence/Outbox/` — `IOutboxStore`, `DbConnectionOutboxStore`, `OutboxEntry`, `OutboxEntryStatus`, `ClaimedOutboxEntry`
- `Persistence/Inbox/` — `IInboxStore`, `DbConnectionInboxStore`
- `Runtime/Outbox/` — `OutboxBehavior`, `OutboxCapture`, `OutboxRelay`, `IOutboxFlusher`, `WrittenOutboxMessage`
- `Runtime/Inbox/` — `InboxInterceptor`, `InboxCleanup`

## Configuration

- `AddDurability()` extension on `ConduitBuilder` — configures database once, nests `AddOutbox()` and `AddInbox()` for feature-specific options. One `IDurabilityDialect` + one `IDurabilityConnectionFactory` shared by all features.
- `DurabilityBuilder` — fluent API: `UseSqlite(cs)`, `UseSqlServer<T>(cs)`, `UsePostgres<T>(cs)`, `UseMySql<T>(cs)`, `AddOutbox(configure?)`, `AddInbox(configure?)`.
- `IDurabilityDialect` — unified SQL dialect interface. Outbox SQL properties prefixed `Outbox*`, inbox properties prefixed `Inbox*`. One implementation per database engine. Row-locking strategies: `FOR UPDATE SKIP LOCKED` (PostgreSQL, MySQL), `ROWLOCK/UPDLOCK/READPAST` (SQL Server), none (SQLite, single-writer).

## Outbox (transactional outbox for reliable F&F delivery)

- `IOutboundInterceptor` in core — generated mediator checks before transport sends for fire-and-forget paths only
- `OutboxCapture` — scoped `IOutboundInterceptor` + `IOutboxFlusher` that buffers `DispatchEnvelope`s. `FlushAsync` sends captured messages immediately from the in-memory buffer after transaction commit, claiming entries via `IOutboxStore.ClaimByMessageIdsAsync` (same row-locking as relay) and dispatching via `TransportDispatcher`. No database read — payloads come from memory. Marks sent on success, increments retry on failure. No-op when nothing captured. Safe to not call — relay picks up entries on its normal schedule.
- `IOutboxFlusher` — user-facing interface with `FlushAsync(CancellationToken)`. Always registered by `AddOutbox()`. Users inject this and call after transaction commit for immediate send. EF Core users can auto-wire via `SaveChangesInterceptor` (documented pattern, not built in).
- `OutboxBehavior<TRequest, TResult>` — pipeline behavior that writes captured messages to outbox store within handler's DB transaction. After writing, calls `OutboxCapture.SetWrittenMessages` to provide the MessageId-to-envelope correlation for `FlushAsync`.
- `OutboxRelay` — `BackgroundService` that polls for pending entries and sends via `TransportDispatcher`
- `IOutboxStore` interface with built-in `DbConnectionOutboxStore` (raw ADO.NET). Includes `ClaimByMessageIdsAsync` for immediate-send entry claiming.

## Inbox (idempotent message processing)

- Application-level message deduplication for all inbound wire messages (commands, queries, events). Prevents duplicate handler execution when brokers redeliver messages. Duplicates are silently discarded — no result caching.
- `InboxInterceptor` — `IInboundInterceptor` gate that checks `MessageContext.Current.MessageId` against `IInboxStore`. If already recorded, returns false (discard). If new, records and returns true (proceed). No-op when no `MessageContext` (local dispatch).
- `IInboxStore` interface with `ExistsAsync`, `RecordAsync`, `DeleteExpiredAsync`, `EnsureTableExistsAsync`. Built-in `DbConnectionInboxStore` (raw ADO.NET). Table schema: `MessageId` (PK) + `ProcessedAt`. INSERT uses upsert/ignore semantics for concurrent safety.
- `InboxCleanup` — `BackgroundService` that periodically removes expired entries.
- `InboxOptions` — `RetentionPeriod` (default 30min), `CleanupInterval` (default 5min).
- **Edge case: record-then-crash** — if process crashes after `RecordAsync` but before handler completes, the message is recorded as processed but was never handled. At-most-once semantics. Standard trade-off (MassTransit/Wolverine same behavior). Users needing at-least-once should use broker-native retry without the inbox.
- **Broker-native dedup** — ASB `RequiresDuplicateDetection`, SQS FIFO `MessageDeduplicationId` are viable alternatives. Not abstracted by Conduit — configure via passthrough broker options.
