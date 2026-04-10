# Dead Letter Queue

- [Why use a dead letter queue?](#why-use-a-dead-letter-queue)
  - [When to use it](#when-to-use-it)
- [How it works](#how-it-works)
  - [Capture](#capture)
  - [Browse](#browse)
  - [Replay](#replay)
  - [Discard](#discard)
- [Two modes](#two-modes)
  - [Conduit database store](#conduit-database-store)
  - [Broker-native store](#broker-native-store)
  - [Choosing a mode](#choosing-a-mode)
- [Setup](#setup)
  - [Database store](#database-store)
  - [Broker-native store](#broker-native-store-1)
  - [Database dialects](#database-dialects)
- [Replay](#replay-1)
  - [Single message](#single-message)
  - [Bulk replay](#bulk-replay)
  - [What happens during replay](#what-happens-during-replay)
  - [Interaction with inbox](#interaction-with-inbox)
- [Browsing and filtering](#browsing-and-filtering)
- [Metadata](#metadata)
- [Transport details](#transport-details)
  - [Azure Service Bus](#azure-service-bus)
  - [RabbitMQ](#rabbitmq)
  - [Amazon SQS](#amazon-sqs)
  - [ActiveMQ Artemis](#activemq-artemis)
  - [Kafka](#kafka)
  - [HTTP and SignalR](#http-and-signalr)
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

The dead letter queue has four operations: **capture**, **browse**, **replay**, and **discard**.

### Capture

When a message fails during inbound handling and retries are exhausted, the transport captures the message into the dead letter store with full metadata — the message body, type, exception details, and MessageContext. The original message is then acknowledged on the broker. It's no longer the broker's concern — Conduit owns the failure now.

```
Message arrives → Handler fails → Write to dead letter store → Acknowledge on broker
```

If the dead letter store write fails (database down, broker DLQ unavailable), the message is abandoned back to the broker for redelivery. No silent message loss.

### Browse

Dead-lettered messages are browsable — non-destructive peek with optional filtering. The database store supports filtering by message type, exception type, and date range. Broker-native stores support browsing with best-effort filtering (varies by broker).

### Replay

Replay re-publishes a dead-lettered message back into the broker for normal processing. The message enters the same inbound path as any new message — through the transport, the pipeline, and the handler. Any available instance of the handling service can pick it up.

Replay mints a new `MessageId` and carries the original as `OriginalMessageId` for tracing. The inbox (if enabled) deduplicates by the new MessageId, so replay is never rejected as a duplicate.

### Discard

Discard removes a message from the dead letter store without replaying. Use this for messages that are permanently invalid — malformed data, obsolete operations, or messages for handlers that no longer exist.

## Two modes

Conduit supports two dead letter store implementations. You choose one based on your infrastructure and preferences — the same way you choose between broker-native deduplication and the Conduit inbox.

### Conduit database store

A database-backed store using the same patterns as the outbox and inbox — `IDeadLetterStore` with `IDeadLetterDialect` implementations for SQL Server, PostgreSQL, SQLite, and MySQL.

- Works with **every transport**, including HTTP and SignalR
- Full query and filter support (type, exception, date range, pagination)
- Replay via Conduit API
- Requires a database

### Broker-native store

Each broker transport can configure its native dead letter mechanism. Conduit's topology manager creates the DLQ resources automatically — you don't need to manually configure dead-letter exchanges, redrive policies, or DLA addresses.

- **Azure Service Bus** — built-in `/$DeadLetterQueue` sub-queue on every queue
- **RabbitMQ** — dead-letter exchange + DLQ queue, bound to app queue via `x-dead-letter-exchange`
- **Amazon SQS** — DLQ queue with redrive policy on the source queue
- **ActiveMQ Artemis** — dead-letter address (DLA) configured via management API

Kafka does not support broker-native DLQ for Conduit's replay API. See [Kafka](#kafka) for details.

### Choosing a mode

| Consideration | Database store | Broker-native store |
|---|---|---|
| Transport support | All 7 transports | ASB, RabbitMQ, SQS, ActiveMQ |
| Query/filter | Full SQL filtering | Best-effort (varies by broker) |
| Replay | Via Conduit API | Via Conduit API |
| Infrastructure | Requires a database | Uses existing broker |
| Operational tooling | Query the database | Broker management UI + Conduit API |

If you're already using the outbox or inbox, you have a database — the DLQ store reuses the same connection and dialect configuration.

If you're running a single broker transport and prefer keeping everything in the broker, the broker-native store avoids the database dependency.

## Setup

### Database store

```csharp
services.AddConduit(conduit =>
{
    conduit.AddDeadLetterQueue(dlq =>
        dlq.UseConnection(() => new SqlConnection(connectionString))
           .UseSqlServer()
    );
});
```

### Broker-native store

Enable per-transport in the transport options:

```csharp
services.AddConduit(conduit =>
{
    conduit.AddEndpoint("orders", endpoint =>
    {
        endpoint.UseRabbitMQ(connectionString, options =>
        {
            options.DeadLetterQueue.Enabled = true;
            options.DeadLetterQueue.MaxDeliveryCount = 5;
        });
    });
});
```

The topology manager creates the necessary DLQ resources during startup. No manual broker configuration needed.

### Database dialects

Same pattern as the outbox:

| Database | Dialect | Method |
|---|---|---|
| SQL Server | `SqlServerDeadLetterDialect` | `.UseSqlServer()` |
| PostgreSQL | `PostgreSqlDeadLetterDialect` | `.UsePostgreSql()` |
| SQLite | `SqliteDeadLetterDialect` | `.UseSqLite()` |
| MySQL | `MySqlDeadLetterDialect` | `.UseMySql()` |

## Replay

### Single message

```csharp
var manager = serviceProvider.GetRequiredService<IDeadLetterManager>();

var result = await manager.ReplayAsync(deadLetterId, cancellationToken);

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
var results = await manager.ReplayAsync(
    new[] { id1, id2, id3 },
    cancellationToken
);

// Replay all messages of a type
var results = await manager.ReplayByTypeAsync(
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
var manager = serviceProvider.GetRequiredService<IDeadLetterManager>();

var page = await manager.QueryAsync(new DeadLetterFilter
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
    var nextPage = await manager.QueryAsync(new DeadLetterFilter
    {
        PageToken = page.NextPageToken,
        PageSize = 25
    }, cancellationToken);
}
```

The database store supports all filter fields. Broker-native stores apply what they can — ASB can filter by message type (via `Subject`), others do best-effort client-side filtering over peek results.

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

For broker-native stores, metadata is stored in transport-native properties — ASB application properties, RabbitMQ headers, SQS message attributes, ActiveMQ application properties.

## Transport details

### Azure Service Bus

**Broker-native DLQ:** Uses the built-in `/$DeadLetterQueue` sub-queue on each app queue. Topology manager sets `MaxDeliveryCount` on queue creation. Conduit enriches the dead-letter call with `DeadLetterReason` and `DeadLetterErrorDescription`.

**Browse:** Native `PeekMessagesAsync` — truly non-destructive, supports sequence number pagination.

**Redeliver:** Sends directly to the app queue (bypasses topic).

### RabbitMQ

**Broker-native DLQ:** Topology manager creates a dead-letter exchange and DLQ queue per app, and sets `x-dead-letter-exchange` on the app queue. Conduit publishes directly to the DLQ with metadata headers (enhanced mode — same approach as Wolverine), then acks the original.

**Browse:** Management HTTP API with `ack_requeue_true`, or `basic.get` + `basic.nack(requeue: true)`.

**Redeliver:** Publishes directly to the app queue via the default exchange (routing key = queue name).

### Amazon SQS

**Broker-native DLQ:** Topology manager creates a DLQ queue and sets a redrive policy (`deadLetterTargetArn` + `maxReceiveCount`) on the source queue. Conduit publishes directly to the DLQ with message attributes (enhanced mode), then deletes the original.

**Browse:** `ReceiveMessage` with `VisibilityTimeout=0` — message stays visible to others.

**Redeliver:** `SendMessage` directly to the app queue URL.

### ActiveMQ Artemis

**Broker-native DLQ:** Topology manager configures dead-letter address (DLA) per address via the management API. Conduit enriches application properties before releasing with `deliveryFailed=true`.

**Browse:** Artemis management `browse` operation — truly non-destructive.

**Redeliver:** Sends directly to the app's anycast/multicast address.

### Kafka

Kafka does not support broker-native DLQ for Conduit's replay API. Kafka topics are append-only logs with sequential offsets — you cannot randomly access, selectively remove, or non-destructively peek individual messages.

Kafka users should use the **database store**. The existing DLQ topic (created by the Kafka topology manager) remains available for users who want to consume it with their own Kafka tooling, but the Conduit `IDeadLetterManager` API requires the database store for Kafka.

**Redeliver:** Produces to the main topic with message type as key. All consumer groups see the message; inbox prevents duplicate processing by non-target services.

### HTTP and SignalR

HTTP and SignalR have no broker-native DLQ concept. Use the **database store**.

**Redeliver (HTTP):** POST to the dispatch/publish endpoint.

**Redeliver (SignalR):** Invokes the hub's `Dispatch`/`Publish` method.

## Limitations

- **One mode per transport.** Choose database store or broker-native — not both on the same transport. The broker's own DLQ mechanism (if it has one) still functions as a safety net for infrastructure failures (process crashes, database unavailability), but Conduit's replay API operates against whichever store you configured.
- **Kafka requires database store** for the replay API. Sequential offset semantics are incompatible with random-access browse and replay.
- **Broker-native filtering is limited.** The database store supports full query filtering. Broker-native stores offer best-effort filtering — results may include messages that don't match all filter criteria.
- **Replay is fire-and-forget.** For request/response messages (commands with results, queries), the handler executes for side effects. There is no caller waiting for the response. The response is returned in the `ReplayResult` for logging but is not delivered anywhere.
- **Replay does not guarantee idempotency.** If the original handler partially succeeded before failing, the replayed attempt is a new execution. Application-level idempotency is the handler's responsibility. The inbox deduplicates by `MessageId`, and replayed messages carry a new `MessageId`.
- **No auto-replay.** Replay is always triggered explicitly via `IDeadLetterManager`. Automatic retry-from-DLQ (with backoff) is a potential future feature.
