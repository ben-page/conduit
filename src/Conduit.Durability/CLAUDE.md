# Conduit.Durability

Durable messaging infrastructure — inbox (inbound deduplication), outbox (outbound capture + relay), and dead letter queue (capture/browse/replay of failed inbound messages). Configured via `conduit.AddDurability()` with one shared database for all features. User docs: [outbox](../../docs/outbox.md), [dead letter queue](../../docs/dead-letter-queue.md).

## Directory layout

- `Configuration/` — `DurabilityBuilder`, `OutboxOptions`, `InboxOptions`, `DeadLetterQueueOptions`
- `Persistence/` — `IDurabilityConnectionFactory`, `ConnectionStringFactory<T>` (internal), `IDurabilityDialect`
- `Persistence/Dialects/` — one class per database implementing `IDurabilityDialect` (SQLite, SQL Server, PostgreSQL, MySQL)
- `Persistence/Outbox/` — `IOutboxStore`, `DbConnectionOutboxStore`, `OutboxEntry`, `OutboxEntryStatus`, `ClaimedOutboxEntry`
- `Persistence/Inbox/` — `IInboxStore`, `DbConnectionInboxStore`
- `Persistence/DeadLetterQueue/` — `DbDeadLetterStore`
- `DeadLetterQueue/` — `IDeadLetters`, `IDeadLetterStore`, `DeadLetterEntry`, `DeadLetterFilter`, `DeadLetterPage`, `ReplayResult`
- `Runtime/Outbox/` — `OutboxBehavior`, `OutboxCapture`, `OutboxRelay`, `IOutboxFlusher`, `WrittenOutboxMessage`
- `Runtime/Inbox/` — `InboxInterceptor`, `InboxCleanup`
- `Runtime/DeadLetterQueue/` — `DeadLetters`, `DeadLetterErrorInterceptor`

## Configuration

- `AddDurability()` extension on `ConduitBuilder` — configures database once, nests `AddOutbox()`, `AddInbox()`, and `AddDeadLetterQueue()` for feature-specific options. One `IDurabilityDialect` + one `IDurabilityConnectionFactory` shared by all features.
- `DurabilityBuilder` — fluent API: `UseSqlite(cs)`, `UseSqlServer<T>(cs)`, `UsePostgres<T>(cs)`, `UseMySql<T>(cs)`, `AddOutbox(configure?)`, `AddInbox(configure?)`, `AddDeadLetterQueue(configure?)`.
- `IDurabilityDialect` — unified SQL dialect interface. Outbox SQL properties prefixed `Outbox*`, inbox `Inbox*`, dead letter `DeadLetter*`. One implementation per database engine. Row-locking strategies: `FOR UPDATE SKIP LOCKED` (PostgreSQL, MySQL), `ROWLOCK/UPDLOCK/READPAST` (SQL Server), none (SQLite, single-writer).

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

## Dead Letter Queue (capture failed inbound messages, browse, replay)

- `IDeadLetters` — user-facing API: `QueryAsync(DeadLetterFilter)`, `GetAsync(id)`, `ReplayAsync(id)` / `ReplayAsync(ids)` / `ReplayByTypeAsync(type)`, `DiscardAsync(id)` / `DiscardAsync(ids)`. Implemented by `Runtime.DeadLetterQueue.DeadLetters`.
- `IDeadLetterStore` + `DbDeadLetterStore` — database persistence (raw ADO.NET, delegates SQL to `IDurabilityDialect.DeadLetter*`). Table `conduit_dead_letters`.
- **Capture**: `BrokerTransportBase` writes failed inbound messages to the store on handler failure (after resilience retries), then acks the broker. If the store write fails, or the DLQ is not registered, the message is abandoned and the broker redelivers. `DeadLetterErrorInterceptor` (`IInboundErrorInterceptor`) drives capture.
- **Replay**: re-publishes via `IConduitTransport.RedeliverAsync` (implemented by every transport) — mints a new `MessageId`, carries `OriginalMessageId`, targets the inbound queue directly. Any handler instance picks it up; the inbox won't reject it (new MessageId).
- **Database-only by design (for now).** Broker-native DLQ stores (wrapping ASB/RabbitMQ/SQS/ActiveMQ native DLQs as the Conduit store) were evaluated and deliberately *not* implemented: non-uniform/fragile browse per broker, best-effort filtering, no reliable counts, racy removal, Kafka incompatible, and it duplicates the DB store unevenly. Not a permanent exclusion — could be added later as an additive per-transport option. The broker's own DLQ still acts as an infrastructure safety net when Conduit's DLQ is not enabled. See [docs/dead-letter-queue.md](../../docs/dead-letter-queue.md).
