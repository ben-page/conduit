# Transactional Outbox

- [Overview](#overview)
- [The problem](#the-problem)
  - [Why not a distributed transaction?](#why-not-a-distributed-transaction)
  - [The solution](#the-solution)
- [How it works](#how-it-works)
- [Setup](#setup)
- [Providing a transaction](#providing-a-transaction)
- [Database dialects](#database-dialects)
- [Custom stores](#custom-stores)
- [Relay configuration](#relay-configuration)
- [What gets captured](#what-gets-captured)
- [Limitations](#limitations)

## Overview

The transactional outbox guarantees at-least-once delivery for fire-and-forget messages. Outbound events and void routed commands are written to an outbox table in the same database transaction as your business data. A background relay delivers them to the broker.

## The problem

When a handler saves business data and publishes a message, two separate systems are involved — your database and the message broker. There's no atomicity guarantee across them:

```
1. Save order to database        ← succeeds
2. Publish OrderCreated event     ← process crashes here — event lost
```

The order exists but no downstream service ever hears about it. Reversing the order doesn't help either — publishing first means the event goes out before the data is committed, and a failed commit leaves consumers acting on data that doesn't exist.

This is the **dual-write problem**: writing to two systems without a shared transaction.

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

OutboxRelay (background service)
  ├── Polls outbox table for pending entries
  ├── Sends each entry via the appropriate transport
  ├── Marks entries as sent on success
  └── Retries on failure, marks as failed after max retries
```

## Setup

Add the `Conduit.Outbox` package and configure inside `AddConduit`:

```xml
<PackageReference Include="Conduit.Outbox" />
```

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddOutbox(outbox =>
    {
        outbox.UseSqlite("Data Source=myapp.db");
    });
});
```

The `AddOutbox` call registers:
- `OutboxCapture` as the `IOutboundInterceptor` (scoped)
- `OutboxBehavior<,>` as a pipeline behavior
- `OutboxRelay` as a background service
- `DbConnectionOutboxStore` with the chosen dialect

## Providing a transaction

The outbox behavior writes entries using the transaction returned by `IOutboxConnectionFactory.GetCurrentTransaction()`. If no transaction is active, the behavior skips persistence (pass-through mode — no crash protection).

The built-in `ConnectionStringFactory` (registered by `UseSqlite()` etc.) always returns `null` from `GetCurrentTransaction()` — it only provides connections for the relay. To get transactional outbox writes, register your own `IOutboxConnectionFactory` that tracks the handler's active transaction:

```csharp
public class AppOutboxConnectionFactory(AppDbContext dbContext) : IOutboxConnectionFactory
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

Register it to replace the built-in factory:

```csharp
builder.Services.AddScoped<IOutboxConnectionFactory, AppOutboxConnectionFactory>();
```

## Database dialects

Each `Use*` method registers the appropriate SQL dialect and connection factory:

| Method | Database | Claiming mechanism |
|---|---|---|
| `UseSqlite(connectionString)` | SQLite | Single-writer (no locking needed) |
| `UseSqlServer<TConnection>(connectionString)` | SQL Server | `WITH (ROWLOCK, UPDLOCK, READPAST)` |
| `UsePostgres<TConnection>(connectionString)` | PostgreSQL | `FOR UPDATE SKIP LOCKED` |
| `UseMySql<TConnection>(connectionString)` | MySQL 8.0+ | `FOR UPDATE SKIP LOCKED` |

SQL Server, PostgreSQL, and MySQL require you to pass the ADO.NET connection type as a generic parameter since Conduit doesn't take a dependency on those provider packages:

```csharp
// SQL Server (requires Microsoft.Data.SqlClient reference)
outbox.UseSqlServer<SqlConnection>("Server=.;Database=MyApp;...");

// PostgreSQL (requires Npgsql reference)
outbox.UsePostgres<NpgsqlConnection>("Host=localhost;Database=MyApp;...");

// MySQL (requires MySqlConnector reference)
outbox.UseMySql<MySqlConnection>("Server=localhost;Database=MyApp;...");
```

The outbox table (`conduit_outbox`) is created automatically on startup. Disable with `outbox.WithAutoCreateTable(false)` if you manage schemas through migrations.

## Custom stores

For non-relational databases (Cosmos DB, MongoDB, DynamoDB), implement `IOutboxStore` directly:

```csharp
public class CosmosOutboxStore : IOutboxStore
{
    // Implement SaveAsync, ClaimPendingAsync, MarkSentAsync,
    // IncrementRetryAsync, MarkFailedAsync, DeleteCompletedAsync,
    // EnsureTableExistsAsync
}
```

Register it:

```csharp
conduit.AddOutbox(outbox =>
{
    outbox.UseStore<CosmosOutboxStore>();
});
```

## Relay configuration

```csharp
conduit.AddOutbox(outbox =>
{
    outbox.UseSqlite("Data Source=myapp.db");
    outbox.WithRelayInterval(TimeSpan.FromSeconds(5));    // polling interval (default: 5s)
    outbox.WithGracePeriod(TimeSpan.FromSeconds(10));     // ignore entries newer than this (default: 10s)
    outbox.WithBatchSize(100);                             // entries per sweep (default: 100)
    outbox.WithMaxRetries(10);                             // before marking failed (default: 10)
    outbox.WithAutoCreateTable(true);                      // create table on startup (default: true)
});
```

The grace period prevents the relay from racing with the handler. After the transaction commits, the entry is in the database — but the relay ignores entries newer than the grace period, giving the handler time to complete any post-commit work.

## What gets captured

The outbox captures **fire-and-forget messages only**:

| Message type | Captured? | Why |
|---|---|---|
| Events (`PublishAsync`) | Yes | Fire-and-forget broadcast |
| Void routed commands (`ICommand`) | Yes | Fire-and-forget to remote endpoint |
| Commands with results (`ICommand<TResult>`) | No | Caller awaits response — can't defer |
| Queries (`IQuery<TResult>`) | No | Caller awaits response — can't defer |
| `[LocalOnly]` events | No | Never sent to transports |

## Limitations

**At-least-once, not exactly-once.** If the relay sends a message but fails to mark it as sent (e.g., database connection blip), it will send again. Consumers must handle duplicates. A future inbox feature will provide application-level deduplication.

**No ordering guarantees.** If message A fails and B succeeds, consumers see B first. For ordered delivery, use broker-native features (ASB sessions, Kafka partitions) or handle ordering at the consumer.

**Relay runs in-process.** The relay is a `BackgroundService` in the same process. If the process is down, pending entries wait until it restarts. Multiple instances of the same app each run a relay — concurrent claiming via `SKIP LOCKED` prevents duplicate sends (except SQLite, which is single-writer).

**No immediate send optimization yet.** Currently all captured messages go through the outbox table and relay. A future optimization will attempt immediate delivery after commit, using the relay only as a safety net.
