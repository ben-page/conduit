# Dead Letter Queue

- [Why use a dead letter queue?](#why-use-a-dead-letter-queue)
  - [When to use it](#when-to-use-it)
- [How it works](#how-it-works)
  - [Capture](#capture)
  - [Browse](#browse)
  - [Replay](#replay)
  - [Discard](#discard)
- [Setup](#setup)
  - [Database dialects](#database-dialects)
- [Replay](#replay-1)
  - [Single message](#single-message)
  - [Bulk replay](#bulk-replay)
  - [What happens during replay](#what-happens-during-replay)
  - [Interaction with inbox](#interaction-with-inbox)
- [Browsing and filtering](#browsing-and-filtering)
- [Metadata](#metadata)
- [Replay by transport](#replay-by-transport)
- [Why database-backed only (for now)](#why-database-backed-only-for-now)
- [Limitations](#limitations)

## Why use a dead letter queue?

When a message fails during handling — the handler throws, a downstream dependency is unavailable, the data is malformed — the message needs to go somewhere. Without a dead letter queue, failed messages are either silently lost or endlessly retried until someone notices.

**You lose visibility.** A command arrives, the handler throws, the broker redelivers a few times, and eventually the message disappears. Nobody knows it happened unless they're watching logs at the right moment.

**You can't recover.** Even if you know a message failed, you have no way to retry it. The original sender has moved on. The message is gone. You're left reconstructing what happened from logs and hoping you can trigger the same operation again.

**Failures cascade silently.** A bug in one handler poisons every message of that type. Without a dead letter queue, those messages vanish. With one, they accumulate visibly — a clear signal that something is wrong and a path to recovery once the bug is fixed.

### When to use it

Use the dead letter queue when:
- **You need visibility into failures.** Messages that fail handling should be inspectable — what failed, why, and when.
- **You need recovery.** Failed messages should be replayable after the root cause is fixed, without requiring the original sender to re-send.
- **You're running broker transports.** HTTP and SignalR propagate errors to the caller. Broker transports (RabbitMQ, ASB, SQS, Kafka, ActiveMQ) process messages asynchronously — the sender isn't waiting.

You may not need it when:
- You're only using local dispatch (no transports). Exceptions propagate to the caller directly.
- Your retry policy (via `Conduit.Resilience`) handles all transient failures and you're comfortable with messages being discarded after retries are exhausted.
- You're in early development and reliability isn't the focus yet.

## How it works

The dead letter queue has four operations: **capture**, **browse**, **replay**, and **discard**. It is backed by a database (the same database the outbox and inbox use). See [Why database-backed only](#why-database-backed-only-for-now) for the reasoning.

### Capture

When a message fails during inbound handling (after any `Conduit.Resilience` retries), the transport writes it to the dead letter store with full metadata — the message body, type, exception details, and MessageContext — then acknowledges the original on the broker. It's no longer the broker's concern; Conduit owns the failure now.

```
Message arrives → Handler fails → Write to dead letter store → Acknowledge on broker
```

If the dead letter store write fails (database down), the message is abandoned back to the broker for redelivery instead. If the dead letter queue is not enabled at all, the transport simply abandons the message and the broker redelivers per its own configuration. No silent message loss.

### Browse

Dead-lettered messages are browsable — a non-destructive query over the store with optional filtering by message type, exception type, and date range, plus pagination.

### Replay

Replay re-publishes a dead-lettered message back into the broker for normal processing. The message enters the same inbound path as any new message — through the transport, the pipeline, and the handler. Any available instance of the handling service can pick it up.

Replay mints a new `MessageId` and carries the original as `OriginalMessageId` for tracing. The inbox (if enabled) deduplicates by the new MessageId, so replay is never rejected as a duplicate.

### Discard

Discard removes a message from the dead letter store without replaying. Use this for messages that are permanently invalid — malformed data, obsolete operations, or messages for handlers that no longer exist.

## Setup

The dead letter queue is part of `Conduit.Durability`. Register the persistence engine once on the `ConduitBuilder` (shared with the outbox, inbox, and audit), then call `AddDeadLetterQueue()` (alongside `AddOutbox()` / `AddInbox()` if you use them):

```csharp
services.AddConduit(conduit =>
{
    conduit.UseSqlServer(connectionString);
    conduit.AddDurability(durability =>
    {
        durability.AddDeadLetterQueue();
    });
});
```

The dead letter table (`conduit_dead_letters`) is created automatically on startup. Disable with `AddDeadLetterQueue(dlq => dlq.AutoCreateTable = false)` if you manage schemas through migrations.

### Database dialects

The dialect is the shared `ISqlDialect` (one per engine, used by the outbox, inbox, DLQ, and audit alike), selected by the engine registered on the `ConduitBuilder`. Each engine ships in its own `Conduit.Persistence.*` package that supplies the ADO.NET driver:

| Engine package | Method |
|---|---|
| `Conduit.Persistence.Sqlite` | `conduit.UseSqlite(connectionString)` |
| `Conduit.Persistence.SqlServer` | `conduit.UseSqlServer(connectionString)` |
| `Conduit.Persistence.Postgres` | `conduit.UsePostgres(connectionString)` |
| `Conduit.Persistence.MySql` | `conduit.UseMySql(connectionString)` |

The engine package carries the provider reference, so no generic connection-type parameter is needed.

## Replay

Inject `IDeadLetters` to browse, replay, and discard.

### Single message

```csharp
var deadLetters = serviceProvider.GetRequiredService<IDeadLetters>();

var result = await deadLetters.ReplayAsync(deadLetterId, cancellationToken);

if (result.Success)
{
    // Message re-published to broker, will be processed by an available handler instance
}
else
{
    // Replay failed — result.ErrorMessage has details
}
```

### Bulk replay

```csharp
// Replay specific messages
var results = await deadLetters.ReplayAsync(
    new[] { id1, id2, id3 },
    cancellationToken
);

// Replay all messages of a type
var results = await deadLetters.ReplayByTypeAsync(
    "Orders.Commands.CreateOrder",
    cancellationToken
);
```

Bulk replay processes messages sequentially — the broker handles distribution and backpressure across handler instances.

### What happens during replay

1. Read the dead letter entry from the store
2. Build a `DispatchEnvelope` with a new `MessageId` (original carried as `OriginalMessageId`)
3. Resolve the transport by the entry's `SourceEndpoint`
4. Call `RedeliverAsync` on the transport — publishes directly to the service's inbound queue
5. Remove the entry from the dead letter store

The message enters the normal inbound processing path. Any available instance of the handling service picks it up. If the handler fails again, the message is captured as a new dead letter entry through the normal flow.

### Interaction with inbox

Replay mints a new `MessageId`, so the inbox will not reject it as a duplicate. The `OriginalMessageId` is preserved for correlation and tracing.

If the original message partially succeeded before failing (handler did some work, then threw), the replayed message is a new attempt. Idempotency at the handler level is the application's responsibility — same as any retry scenario.

## Browsing and filtering

```csharp
var deadLetters = serviceProvider.GetRequiredService<IDeadLetters>();

var page = await deadLetters.QueryAsync(new DeadLetterFilter
{
    MessageType = "Orders.Commands.CreateOrder",
    Since = DateTimeOffset.UtcNow.AddHours(-24),
    PageSize = 25
}, cancellationToken);

foreach (var entry in page.Entries)
{
    Console.WriteLine($"{entry.MessageType} — {entry.ExceptionMessage} — {entry.DeadLetteredAt}");
}

// Next page
if (page.NextPageToken is not null)
{
    var nextPage = await deadLetters.QueryAsync(new DeadLetterFilter
    {
        PageToken = page.NextPageToken,
        PageSize = 25
    }, cancellationToken);
}
```

`DeadLetterFilter` supports `MessageType`, `ExceptionType`, `Since`, `Until`, `PageSize` (default 25), and `PageToken`. All filters are applied in SQL.

## Metadata

Every dead-lettered message carries:

| Field | Description |
|---|---|
| `Id` | Unique identifier for this dead letter entry |
| `MessageType` | Full type name (e.g., `Orders.Commands.CreateOrder`) |
| `Body` | Serialized message body (BinaryData) |
| `SourceEndpoint` | Endpoint name the message arrived through |
| `ExceptionType` | Full type name of the exception that caused the failure |
| `ExceptionMessage` | Exception message text |
| `MessageId` | Original MessageContext.MessageId |
| `CausationId` | Original MessageContext.CausationId |
| `ConversationId` | Original MessageContext.ConversationId |
| `AttemptCount` | Number of times this message has been dead-lettered |
| `DeadLetteredAt` | Timestamp when the message was captured |

## Replay by transport

Replay re-publishes through the source transport's `RedeliverAsync`, which targets the service's inbound queue directly so any available instance picks the message up.

| Transport | Redeliver mechanism |
|---|---|
| **Azure Service Bus** | Sends directly to the app queue (bypasses the topic). |
| **RabbitMQ** | Publishes to the app queue via the default exchange (routing key = queue name). |
| **Amazon SQS** | `SendMessage` directly to the app queue URL. |
| **ActiveMQ Artemis** | Sends directly to the app's anycast/multicast address. |
| **Kafka** | Produces to the main topic with message type as key. All consumer groups see it; the inbox prevents duplicate processing by non-target services. |
| **HTTP** | POST to the dispatch/publish endpoint. |
| **SignalR** | Invokes the hub's `Dispatch`/`Publish` method. |

## Why database-backed only (for now)

The DLQ has a single backing store: the Conduit database. We evaluated also wrapping each broker's *native* dead letter facility (ASB `/$DeadLetterQueue`, RabbitMQ dead-letter exchanges, SQS redrive, ActiveMQ DLA) as an alternative store, and decided against it **for now**. The deciding factors:

- **Non-uniform, fragile browsing.** Non-destructive browse needs a different mechanism per broker — ASB `PeekMessagesAsync` (clean), RabbitMQ via the Management HTTP API or `basic.get` + `nack(requeue)`, SQS via `ReceiveMessage` with `VisibilityTimeout=0`, ActiveMQ via a management `browse`. Several only offer approximate pagination.
- **Best-effort filtering.** Only a SQL store can apply arbitrary filters server-side. Broker-native browsing filters client-side over peeked pages.
- **No reliable counts.** Most brokers can't return a total count without consuming the queue.
- **Racy removal/replay.** Pulling one specific message out of a broker DLQ (receive-by-handle/sequence + ack) isn't transactional and races with concurrent browsers.
- **Kafka can't participate** — append-only, offset-addressed logs are incompatible with random-access browse/remove/replay.
- **It duplicates the database store unevenly.** The database store already works with every transport (including HTTP and SignalR) and gives full query, filter, count, and replay. Broker-native would add per-transport topology and three operations (write/query/remove) × four brokers for a strictly worse, inconsistent browse experience.

This isn't a permanent exclusion — broker-native capture could be added later as an additive per-transport option if a strong use case appears (e.g. avoiding a database dependency entirely). Today the cost/inconsistency isn't worth it.

Note that the broker's own DLQ still functions as an infrastructure safety net: if you don't enable Conduit's DLQ, failed messages are abandoned and the broker redelivers (and dead-letters) per its own configuration — Conduit just doesn't browse or replay through it.

## Limitations

- **Requires a database.** The DLQ shares the durability database. A stateless service with no database can't use it (use broker-native redelivery/DLQ at the infrastructure level instead).
- **Replay is fire-and-forget.** For request/response messages (commands with results, queries), the handler executes for side effects — there is no caller waiting for the response. The response is returned in `ReplayResult` for logging but is not delivered anywhere.
- **Replay does not guarantee idempotency.** If the original handler partially succeeded before failing, the replayed attempt is a new execution. Application-level idempotency is the handler's responsibility. The inbox deduplicates by `MessageId`, and replayed messages carry a new `MessageId`.
- **No auto-replay.** Replay is always triggered explicitly via `IDeadLetters`. Automatic retry-from-DLQ (with backoff) is a potential future feature.
