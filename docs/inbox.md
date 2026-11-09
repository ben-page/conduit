# Inbox (Idempotent Message Processing)

- [Why use an inbox?](#why-use-an-inbox)
  - [When to use it](#when-to-use-it)
- [How it works](#how-it-works)
- [Setup](#setup)
- [Database dialects](#database-dialects)
- [Configuration](#configuration)
- [Semantics: at-most-once on crash](#semantics-at-most-once-on-crash)
- [Relationship to the outbox](#relationship-to-the-outbox)
- [Broker-native deduplication](#broker-native-deduplication)
- [Limitations](#limitations)

## Why use an inbox?

Message brokers deliver **at least once**. A consumer that crashes after handling a message but before acknowledging it will receive that message again on restart; competing consumers and visibility-timeout expiry produce the same effect. Without protection, the handler runs twice — charging a card twice, sending two emails, double-incrementing a counter.

The inbox makes inbound message processing **idempotent at the framework level**. It records the `MessageId` of every message it has processed and silently discards any message it has seen before, so the handler runs at most once per logical message — no matter how many times the broker redelivers it.

### When to use it

Use the inbox when:
- **Your handlers have side effects that must not repeat** — writes, payments, notifications, external API calls.
- **You're consuming from a broker** (RabbitMQ, ASB, SQS, ActiveMQ). At-least-once delivery means duplicates are a question of *when*, not *if*.
- **You want one consistent dedup mechanism** across all transports rather than configuring each broker's native feature.

You may not need it when:
- Your handlers are naturally idempotent (e.g., an upsert keyed by a field in the message, or a cache refresh that's safe to repeat).
- You're only using local in-process dispatch — there's no wire, no redelivery, nothing to deduplicate.
- You rely on a broker-native deduplication feature instead (see [below](#broker-native-deduplication)).

## How it works

The inbox is an `IInboundInterceptor` — a **gate** that runs on the inbound wire path *before* the message payload is deserialized, so a duplicate costs almost nothing. It keys off `MessageContext.MessageId` (a GUID present on every message) and consults the inbox store:

```
Inbound wire message arrives (transport provides MessageId)
  └── InboxInterceptor gate runs (pre-deserialization)
        ├── MessageId already in store?  → discard (gate returns false); broker ACKs, no handler run
        └── new MessageId?               → record it, then proceed to deserialize + dispatch
```

- **Dedup happens before the handler** — that's the whole point; it prevents duplicate side effects, not just duplicate results.
- **All inbound wire messages are deduplicated** — commands, queries, and events alike.
- **Duplicates are silently discarded.** There is no result caching — a duplicate is dropped, not answered with the original response. (This matches MassTransit and Wolverine.)
- **Local dispatch is unaffected** — the gate only runs on transport-inbound messages; in-process dispatch never hits it.

A background `InboxCleanup` service periodically deletes records older than the retention window, so the table doesn't grow without bound.

## Setup

The inbox is part of `Conduit.Durability`. Register the persistence engine once on the `ConduitBuilder` (shared with the outbox, DLQ, audit trail, and jobs), then call `AddInbox()` (alongside `AddOutbox()` / `AddDeadLetterQueue()` if you use them):

```xml
<PackageReference Include="Conduit.Durability" />
<PackageReference Include="Conduit.Persistence.Sqlite" />
```

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.UseSqlite("Data Source=myapp.db");
    conduit.AddDurability(durability =>
    {
        durability.AddInbox();
    });
});
```

`AddInbox()` registers the `InboxInterceptor` gate, the `IInboxStore` (`DbConnectionInboxStore`), and the `InboxCleanup` background service. The inbox is **all-or-nothing** — once enabled, every inbound wire message is deduplicated.

## Database dialects

The inbox uses the shared `ISqlDialect` (one per engine, used by the outbox, inbox, DLQ, audit trail, and jobs alike), selected by the engine registered on the `ConduitBuilder`. Each engine ships in its own `Conduit.Persistence.*` package that supplies the ADO.NET driver:

| Engine package | Method |
|---|---|
| `Conduit.Persistence.Sqlite` | `conduit.UseSqlite(connectionString)` |
| `Conduit.Persistence.SqlServer` | `conduit.UseSqlServer(connectionString)` |
| `Conduit.Persistence.Postgres` | `conduit.UsePostgres(connectionString)` |
| `Conduit.Persistence.MySql` | `conduit.UseMySql(connectionString)` |

The engine package carries the provider reference, so no generic connection-type parameter is needed.

The inbox table (`conduit_inbox` — `MessageId` primary key + `ProcessedAt`) is created automatically on startup. The insert uses engine-specific ignore/upsert semantics (SQLite `INSERT OR IGNORE`, PostgreSQL `ON CONFLICT DO NOTHING`, MySQL `INSERT IGNORE`, SQL Server insert-where-not-exists). This single atomic insert *is* the deduplication decision — the gate records and tests for a duplicate in one operation (the row was newly inserted ⇒ proceed; it already existed ⇒ discard), with no separate existence check. That closes the window where two concurrent redeliveries of the same `MessageId` could otherwise both pass a check and run the handler twice, so duplicate processing is impossible even under competing consumers.

## Configuration

```csharp
conduit.UseSqlite("Data Source=myapp.db");
conduit.AddDurability(durability =>
{
    durability.AddInbox(inbox =>
    {
        inbox.RetentionPeriod = TimeSpan.FromMinutes(30);  // how long processed IDs are kept (default: 30m)
        inbox.CleanupInterval = TimeSpan.FromMinutes(5);   // how often expired IDs are purged (default: 5m)
    });
});
```

`RetentionPeriod` must comfortably exceed your broker's maximum redelivery window. If a duplicate can arrive later than the retention period (e.g. a long SQS visibility timeout, or a message parked for hours), raise it — otherwise the original record may be purged before the duplicate shows up, and the duplicate will be processed.

## Semantics: at-most-once on crash

The inbox records the `MessageId` **before** the handler runs. If the process crashes *after* recording but *before* the handler completes, the message is marked processed yet was never fully handled — it will not be retried. This is **at-most-once** processing.

This is the standard inbox trade-off (MassTransit and Wolverine behave the same). Recording *after* the handler would instead allow duplicate execution on a crash. At-most-once is the safer default for side-effect-heavy handlers. If you need at-least-once semantics, rely on broker-native redelivery **without** the inbox and make your handlers idempotent another way.

## Relationship to the outbox

The inbox and outbox are complementary halves of reliable messaging, and share one database:

- The **[outbox](outbox.md)** guarantees a message is *sent* reliably (no lost or premature publishes).
- The **inbox** guarantees a message is *processed* once on the receiving side (no duplicate handling).

Together they approximate exactly-once *effects* over an at-least-once transport. Enable whichever side a given service needs — a pure producer needs only the outbox; a pure consumer needs only the inbox; a service that does both can enable both on the same `AddDurability` block.

## Broker-native deduplication

Some brokers offer native deduplication — Azure Service Bus `RequiresDuplicateDetection`, Amazon SQS FIFO `MessageDeduplicationId`. These are viable alternatives (or belt-and-suspenders companions) to the Conduit inbox. Conduit does not abstract them — configure them through the transport's passthrough broker options. Use broker-native dedup when you'd rather not run a database, or the inbox when you want one uniform mechanism across every transport.

## Limitations

- **Requires a database.** The inbox shares the durability database. A stateless service with no database should use broker-native dedup instead.
- **At-most-once on crash** — see [above](#semantics-at-most-once-on-crash). The record-then-handle ordering trades duplicate-execution risk for lost-on-crash risk.
- **No result caching.** Duplicates are discarded, not answered. For request/response over a broker, a duplicate request produces no second reply — the caller relies on the first.
- **Retention must outlast redelivery.** If `RetentionPeriod` is shorter than the broker's redelivery window, a late duplicate can slip through after its record is purged.
- **All-or-nothing.** There's no per-type opt-out; enabling the inbox deduplicates every inbound wire message.
