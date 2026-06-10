# Transactional Outbox

- [Why use an outbox?](#why-use-an-outbox)
  - [When to use it](#when-to-use-it)
- [The dual-write problem](#the-dual-write-problem)
  - [Why not a distributed transaction?](#why-not-a-distributed-transaction)
  - [The solution](#the-solution)
- [How it works](#how-it-works)
- [Setup](#setup)
- [Providing a transaction](#providing-a-transaction)
- [Database dialects](#database-dialects)
- [Custom stores](#custom-stores)
- [Relay configuration](#relay-configuration)
- [What gets captured](#what-gets-captured)
- [Immediate send](#immediate-send)
- [Limitations](#limitations)

## Why use an outbox?

Without an outbox, publishing a message is a best-effort operation. Several things can go wrong:

**Your application crashes.** The handler saves an order to the database, then calls `PublishAsync`. The process crashes — or the container is killed, or a deployment rolls through — after the database commit but before the message reaches the broker. The order exists. No other service knows about it.

**The broker is down.** RabbitMQ is restarting, the Kafka cluster is unreachable, or the Azure Service Bus namespace is throttling. The database commit succeeds but the publish call fails. The message is lost unless you catch the exception and retry — but retry logic is fragile and doesn't survive process restarts.

**You publish before committing.** Publishing first avoids losing the message, but creates a worse problem. A downstream service receives `OrderCreated`, queries your API for the order, and gets a 404 — because the transaction hasn't committed yet, or it rolled back. Now downstream services are reacting to data that doesn't exist.

These aren't edge cases. In a distributed system with multiple services, they're inevitable. The question is whether your architecture handles them or hopes they don't happen.

### When to use it

Use the outbox when:
- **Reliability matters.** Messages must not be silently lost when the broker is temporarily unavailable or the process crashes at the wrong moment.
- **Consistency matters.** Downstream services should never see events about data that hasn't been committed, and committed data should always produce its events.
- **You have a database.** The outbox shares a transaction with your business data. If your service is stateless (no database), the outbox doesn't apply — the broker's own delivery guarantees are sufficient.

You may not need it when:
- You're comfortable with best-effort delivery and can tolerate occasional lost messages.
- Your messages are idempotent regenerations (e.g., a cache invalidation that can be repeated on the next request).
- You're in a development or prototyping phase where reliability isn't the focus yet.

## The dual-write problem

The root cause is that your handler writes to two separate systems — the database and the message broker — without a shared transaction. This is called the **dual-write problem**.

```
1. Save order to database        ← succeeds
2. Publish OrderCreated event     ← process crashes here — event lost
```

No matter which order you do the writes, one of them can fail after the other succeeds, leaving the two systems inconsistent.

### Why not a distributed transaction?

Two-phase commit (2PC) could make the database and broker atomic, but it's not viable in practice:

| Broker | 2PC Support |
|---|---|
| Azure Service Bus | Only with SQL Server on Windows via MSDTC |
| RabbitMQ | None — publisher confirms are async, not transactional with a DB |
| Kafka | Kafka-to-Kafka only — can't enlist a database |
| Amazon SQS | None |
| ActiveMQ | Requires MSDTC (AmqpNetLite doesn't support enlistment) |

Even where technically possible (ASB + SQL Server), MSDTC is Windows-only, incompatible with containers and cloud-native deployments, and adds a single point of failure. The industry moved away from 2PC for this reason.

### The solution

The outbox pattern sidesteps the problem by writing the message to your database instead of the broker. Since both writes (business data + outbox entry) hit the same database, they share a single transaction — no distributed coordination needed:

```
1. Save order to database        ← same transaction
2. Write outbox entry             ← same transaction
3. Commit                         ← atomic, single database
4. Relay sends to broker          ← eventually, with retries
```

The tradeoff is eventual consistency — messages arrive at the broker with a small delay (the relay polling interval). But delivery is guaranteed as long as the database is durable.

## How it works

```
Handler executes (in managed transaction)
  ├── Business data written to DB
  ├── Handler calls PublishAsync(event)
  │     → IOutboundInterceptor captures envelope (not sent yet)
  ├── OutboxBehavior writes captured envelopes to outbox table
  └── Transaction commits (business data + outbox entries, atomic)

IOutboxFlusher.FlushAsync (optional — immediate send)
  ├── Claims entries by MessageId (same row-locking as relay)
  ├── Sends from in-memory buffer (no DB read)
  ├── Marks entries as sent on success
  └── Increments retry on failure — relay picks up later

OutboxRelay (background service — safety net)
  ├── Polls outbox table for pending entries
  ├── Sends each entry via the appropriate transport
  ├── Marks entries as sent on success
  └── Retries on failure, marks as failed after max retries
```

## Setup

Add the `Conduit.Durability` package and configure inside `AddConduit`. The persistence engine is registered once on the `ConduitBuilder` (shared with the inbox, DLQ, and audit), then `AddOutbox()` enables the outbox on it:

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
        durability.AddOutbox();
    });
});
```

The `AddOutbox()` call registers:
- `OutboxCapture` as both `IOutboundInterceptor` and `IOutboxFlusher` (scoped)
- `OutboxBehavior<,>` as a pipeline behavior
- `OutboxRelay` as a background service
- `DbConnectionOutboxStore` with the chosen dialect

## Providing a transaction

A transaction-aware connection factory is **mandatory** for the outbox. The outbox behavior writes entries using the transaction returned by `IConduitConnectionFactory.GetCurrentTransaction()` so that the business data and the outbox entry commit atomically.

The connection factory supplied by the engine package (registered by `conduit.UseSqlite()`, `conduit.UseSqlServer()`, etc.) is non-transactional: it always returns `null` from `GetCurrentTransaction()` — it only opens fresh connections for the relay and store sweeps and has no visibility into the handler's transaction. If outbox capture ran against it, it would silently persist **nothing** — the outbox would appear configured while protecting nothing.

To prevent that, **enabling `AddOutbox()` while the engine's non-transactional factory is the active `IConduitConnectionFactory` fails fast at startup** with an `InvalidOperationException` explaining that a transaction-aware factory is required. Register your own factory that tracks the handler's active transaction:

```csharp
public class AppDurabilityConnectionFactory(AppDbContext dbContext) : IConduitConnectionFactory
{
    public Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=myapp.db");
        connection.Open();
        return Task.FromResult<DbConnection>(connection);
    }

    public DbTransaction? GetCurrentTransaction()
        => dbContext.Database.CurrentTransaction?.GetDbTransaction();
}
```

Register it after `AddConduit` so it overrides the built-in factory:

```csharp
builder.Services.AddScoped<IConduitConnectionFactory, AppDurabilityConnectionFactory>();
```

## Database dialects

Each `Use*` method registers the appropriate SQL dialect and connection factory. Claiming is **atomic**: a single `UPDATE` transitions pending rows to a `Claiming` status tagged with a per-worker token, so a row can be claimed (and dispatched) by exactly one worker — the relay and the immediate-send flush can never double-send. A claim that is not resolved within 5 minutes (e.g. a crashed worker) becomes re-claimable. The per-engine locking applied inside the claim:

| Method | Database | Claiming mechanism |
|---|---|---|
| `conduit.UseSqlite(connectionString)` | SQLite | Single-writer (atomic `UPDATE` + read-back by token) |
| `conduit.UseSqlServer(connectionString)` | SQL Server | `WITH (ROWLOCK, UPDLOCK, READPAST)` in the claim subquery |
| `conduit.UsePostgres(connectionString)` | PostgreSQL | `FOR UPDATE SKIP LOCKED` in the claim subquery |
| `conduit.UseMySql(connectionString)` | MySQL 8.0+ | `FOR UPDATE SKIP LOCKED` in the claim subquery |

The outbox table carries two columns supporting this: `ClaimedAt` and `ClaimToken`.

These `Use*` methods are registered once on the `ConduitBuilder` (the engine is shared by the outbox, inbox, dead letter queue, and audit). Each engine ships in its own `Conduit.Persistence.*` package that carries the ADO.NET driver, so no generic connection-type parameter is needed:

```csharp
// SQL Server (Conduit.Persistence.SqlServer package)
conduit.UseSqlServer("Server=.;Database=MyApp;...");

// PostgreSQL (Conduit.Persistence.Postgres package)
conduit.UsePostgres("Host=localhost;Database=MyApp;...");

// MySQL (Conduit.Persistence.MySql package)
conduit.UseMySql("Server=localhost;Database=MyApp;...");

conduit.AddDurability(durability => durability.AddOutbox());
```

The outbox table (`conduit_outbox`) is created automatically on startup. Disable with `AddOutbox(o => o.AutoCreateTable = false)` if you manage schemas through migrations.

## Custom stores

For non-relational databases (Cosmos DB, MongoDB, DynamoDB), implement `IOutboxStore` directly:

```csharp
public class CosmosOutboxStore : IOutboxStore
{
    // Implement SaveAsync, ClaimPendingAsync, ClaimByMessageIdsAsync, MarkSentAsync,
    // IncrementRetryAsync, MarkFailedAsync, DeleteCompletedAsync, EnsureTableExistsAsync
}
```

Register it as `IOutboxStore` after `AddConduit` so it overrides the built-in `DbConnectionOutboxStore`:

```csharp
builder.Services.AddScoped<IOutboxStore, CosmosOutboxStore>();
```

## Relay configuration

```csharp
conduit.UseSqlite("Data Source=myapp.db");
conduit.AddDurability(durability =>
{
    durability.AddOutbox(outbox =>
    {
        outbox.RelayInterval = TimeSpan.FromSeconds(5);    // polling interval (default: 5s)
        outbox.GracePeriod = TimeSpan.FromSeconds(10);     // ignore entries newer than this (default: 10s)
        outbox.BatchSize = 100;                            // entries per sweep (default: 100)
        outbox.MaxRetries = 10;                            // before marking failed (default: 10)
        outbox.AutoCreateTable = true;                     // create table on startup (default: true)
    });
});
```

The grace period prevents the relay from racing with the handler. After the transaction commits, the entry is in the database — but the relay ignores entries newer than the grace period, giving the handler time to complete any post-commit work.

## What gets captured

The outbox captures **fire-and-forget messages only** — messages where no reply is expected and the caller doesn't wait for confirmation:

| Message type | Captured? | Why |
|---|---|---|
| Events (`PublishAsync`) | Yes | Always fire-and-forget on the transport side |
| `[FireAndForget]` void routed commands | Yes | No reply expected — true fire-and-forget |
| Non-`[FireAndForget]` void commands (`ICommand`) | No | Uses request/response to confirm handler execution |
| Commands with results (`ICommand<TResult>`) | No | Caller awaits response — can't defer |
| Queries (`IQuery<TResult>`) | No | Caller awaits response — can't defer |
| `[LocalOnly]` events | No | Never sent to transports |

## Immediate send

By default, outbox messages wait for the relay's next poll cycle before reaching the broker. The immediate send optimization bypasses this delay by sending messages from the in-memory buffer right after the transaction commits, using the relay only as a safety net for failures.

### Usage

Inject `IOutboxFlusher` and call `FlushAsync` after committing your transaction:

```csharp
public class PlaceOrderHandler(
    AppDbContext dbContext,
    IMediator mediator,
    IOutboxFlusher flusher) : ICommandHandler<PlaceOrderCommand, Guid>
{
    public async Task<Guid> HandleAsync(PlaceOrderCommand command, CancellationToken cancellationToken)
    {
        var order = new Order(command.Product);
        dbContext.Orders.Add(order);

        await mediator.PublishAsync(new OrderPlacedEvent(order.Id, command.Product), cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await flusher.FlushAsync(cancellationToken);

        return order.Id;
    }
}
```

### Behavior

`FlushAsync` claims the outbox entries written during the current scope using the same atomic claim as the relay (`ClaimByMessageIds`, which transitions each row to `Claiming` under a per-worker token). It then sends each entry via the appropriate transport using the in-memory envelope — no database read needed. Successfully sent entries are marked as `Sent`. If a transport fails, the entry's retry count is incremented and the claim is released so the relay picks it up on its next sweep.

`FlushAsync` is a no-op when nothing was captured during the scope, and safe to call unconditionally. If the relay has already claimed an entry before `FlushAsync` runs, the claim returns nothing and the call completes without sending duplicates.

### EF Core auto-flush

For EF Core applications, a `SaveChangesInterceptor` can call `FlushAsync` automatically after every successful save:

```csharp
public class OutboxFlushInterceptor(IOutboxFlusher flusher) : SaveChangesInterceptor
{
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await flusher.FlushAsync(cancellationToken);
        return result;
    }
}
```

Register it:

```csharp
builder.Services.AddScoped<OutboxFlushInterceptor>();

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.AddInterceptors(serviceProvider.GetRequiredService<OutboxFlushInterceptor>());
});
```

## Limitations

**At-least-once, not exactly-once.** If the relay sends a message but fails to mark it as sent (e.g., database connection blip), it will send again. Consumers must handle duplicates. The [inbox](inbox.md) — also part of `Conduit.Durability`, enabled via `durability.AddInbox()` — provides application-level deduplication.

**No ordering guarantees.** If message A fails and B succeeds, consumers see B first. For ordered delivery, use broker-native features (ASB sessions, Kafka partitions) or handle ordering at the consumer.

**Relay runs in-process.** The relay is a `BackgroundService` in the same process. If the process is down, pending entries wait until it restarts. Multiple instances of the same app each run a relay — atomic claiming (an `UPDATE` to a `Claiming` status tagged with a per-worker token) ensures each entry is claimed and sent by exactly one worker, on every supported engine including SQLite. The relay and the immediate-send `FlushAsync` likewise never double-send the same entry.

**Immediate send is best-effort.** `FlushAsync` attempts to send messages immediately, but the relay is still the safety net. If the process crashes between commit and `FlushAsync`, or if `FlushAsync` itself fails, the relay delivers the messages on its next sweep. This means messages may occasionally arrive with relay-scale latency even when immediate send is configured.
