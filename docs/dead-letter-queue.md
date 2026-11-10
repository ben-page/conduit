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
- **You're running broker transports.** HTTP and SignalR propagate errors to the caller. Broker transports (RabbitMQ, ASB, SQS, ActiveMQ) process messages asynchronously — the sender isn't waiting.

You may not need it when:
- You're only using local dispatch (no transports). Exceptions propagate to the caller directly.
- Your retry policy (via `Conduit.Resilience`) handles all transient failures and you're comfortable with messages being discarded after retries are exhausted.
- You're in early development and reliability isn't the focus yet.

## How it works

The dead letter queue has four operations: **capture**, **browse**, **replay**, and **discard**. It is backed by a database (the same database the outbox and inbox use). See [Why database-backed only](#why-database-backed-only-for-now) for the reasoning.

### Capture

When an inbound message cannot be handled (after any `Conduit.Resilience` retries), the transport writes it to the dead letter store with full metadata — the message body, type, exception details, and MessageContext — then acknowledges the original on the broker. It's no longer the broker's concern; Conduit owns the failure now.

```
Message arrives → Handler fails → Write to dead letter store → Acknowledge on broker
```

What counts is what could not be handled, never what was refused. The last column is what happens when nothing captures the message: no dead letter queue is registered, or the store write fails (database down).

| Outcome | Captured? | When not captured |
|---|---|---|
| The message fails **before dispatch**: its body is not JSON, or its DI scope or the mediator cannot be built | Yes | Acknowledged, logged at Error: it never reached a handler, and a redelivery would fail the same way, forever |
| An **event this host cannot read**: valid JSON its type cannot read (a wrong-typed field, a JSON `null`), or a type it does not know (`BadRequestException`, `ContractMismatchException`) | Yes | Acknowledged, logged at Warning as a refused event, for the same reason |
| An event listener throws | Yes | Abandoned: RabbitMQ rejects it to the system's [dead-letter exchange](transports/rabbitmq.md#failed-messages); Amazon SQS delivers it again a visibility timeout later, and moves it to the queue's [dead-letter queue](transports/amazon-sqs.md#failed-messages) after `MaxReceiveCount` deliveries; the other brokers redeliver it per their own configuration |
| A request's handler fails **unexpectedly** (the caller is answered `InternalError`) | Yes, with the answer's support reference in `Reference` | Acknowledged: the caller already has its error reply |
| A request is **refused**: the handler (or a gate, or a translator) throws a `ConduitException` — `NotFoundException` (404), `ConflictException` (409), `PreconditionFailedException` (412), `ForbiddenException`, `BadRequestException`, ... | No: a refusal is the handler's answer, already delivered to the caller, and a replay would be refused again | Acknowledged |
| An event is **refused by a gate**: the type filter's `ForbiddenException`, or another gate's refusal that is not a bad request | No: a replay would be refused again | Acknowledged |
| A `[FireAndForget]` command's handler fails **unexpectedly** (its result is `InternalError`): no caller is waiting to hear it | Yes, with its support reference in `Reference` | Abandoned, as an event whose listener throws is, so its handler runs again until the broker dead-letters it: RabbitMQ rejects it once to the [dead-letter exchange](transports/rabbitmq.md#failed-messages); ActiveMQ redelivers it up to `max-delivery-attempts` (10) with no delay; Azure Service Bus redelivers it at once up to the entity's maximum delivery count (10); Amazon SQS delivers it again a visibility timeout apart up to `MaxReceiveCount` (5). A handler with side effects should be idempotent, or the host should use the [inbox](inbox.md) or this dead-letter queue |
| A `[FireAndForget]` command is a **bad request**: valid JSON its type cannot read (`ContractMismatchException`, `BadRequestException`), or any other `BadRequestException` refusal, a failed validation among them | Yes | Acknowledged: a redelivery would fail the same way |
| A `[FireAndForget]` command is **refused** otherwise: the handler (or a gate, or a translator) throws a `ConduitException` — the type filter's `ForbiddenException`, `NotFoundException`, `ConflictException`, ... | No: a replay would be refused again | Acknowledged, logged once at Warning (`FireAndForgetCommandRefused`, naming the type and the code), since no caller is waiting for the refusal |

For a request or a `[FireAndForget]` command that failed unexpectedly, the runtime logs the real exception under a support reference, and answers a request's caller with that reference rather than the exception. The entry's `ExceptionType`/`ExceptionMessage` therefore name the answer (`[InternalError] An unexpected error occurred.`), and its `Reference` is the value the caller saw and the logged exception carries — search your logs for it.

### Browse

Dead-lettered messages are browsable — a non-destructive query over the store with optional filtering by message type, exception type, and date range, plus pagination.

### Replay

Replay re-publishes a dead-lettered message back into the broker for normal processing. The message enters the same inbound path as any new message — through the transport, the pipeline, and the handler. Any available instance of the handling service can pick it up.

Replay carries the ids the message first arrived with — its `MessageId`, `CausationId` and `ConversationId` — so its handling, and its audit trail, continue the original's lineage instead of starting a conversation of their own. See [Interaction with inbox](#interaction-with-inbox) for what that means for deduplication.

### Discard

Discard removes a message from the dead letter store without replaying. Use this for messages that are permanently invalid — malformed data, obsolete operations, or messages for handlers that no longer exist.

## Setup

The dead letter queue is part of `Conduit.Durability`. Register the persistence engine once on the `ConduitBuilder` (shared with the outbox, inbox, audit trail, and jobs), then call `AddDeadLetterQueue()` (alongside `AddOutbox()` / `AddInbox()` if you use them):

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

The dialect is the shared `ISqlDialect` (one per engine, used by the outbox, inbox, DLQ, audit trail, and jobs alike), selected by the engine registered on the `ConduitBuilder`. Each engine ships in its own `Conduit.Persistence.*` package that supplies the ADO.NET driver:

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
2. Build a `DispatchEnvelope` from the stored body and type
3. Resolve the transport by the entry's `SourceEndpoint`
4. Call `RedeliverAsync` on the transport with the entry's `MessageId`, `CausationId` and `ConversationId` — it publishes directly to the service's inbound queue, those ids in the broker's native properties (an entry missing any of the three, captured outside a context, is sent with a fresh message id)
5. Remove the entry from the dead letter store

The message enters the normal inbound processing path. Any available instance of the handling service picks it up. A replayed **event** whose listener fails again is captured as a new dead letter entry through the normal flow, under the same ids. A replayed **command or query** arrives without a reply address, so it is settled as a `[FireAndForget]` command is: its result reaches no one, a handler that fails unexpectedly again is captured as a new entry under the same ids, and a refusal is acknowledged.

### Interaction with inbox

The [inbox](inbox.md) deduplicates by `MessageId`, and a replay carries the original one. The inbox records a message's id when it admits the message, before its handler runs, and **a failed dispatch releases that record**: the transport offers every failure to the inbox as well as to the dead letter queue, so the dead letter queue stores the message and the inbox forgets it, in either order. A replay therefore runs whenever it is sent, inside `InboxOptions.RetentionPeriod` or after it, as does a broker redelivery of a failure nothing captured. A dispatch that succeeded stays recorded for `RetentionPeriod`, so a duplicate of it is still discarded. See [When a dispatch fails](inbox.md#when-a-dispatch-fails).

What each capture leaves in the inbox:

- **A handler or listener that threw, or a request answered `InternalError`**: the gate recorded the id, and the failure releases it.
- **An event this host cannot read**: gates run before the body is read, so the gate had already recorded the id when the read failed. The capture releases it, as any other failure's does, and the replay (once the contract is fixed) runs.
- **A message that failed before dispatch** (a body that is not JSON, a scope or mediator that could not be built): the gate never ran, so this delivery recorded nothing and releases nothing.

If the release fails (the inbox database is unreachable; logged at Warning, `InboxReleaseFailed`), the id stays recorded, and a replay inside the retention period is discarded as a duplicate while the entry is still removed.

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

`DeadLetterFilter` supports `MessageType`, `ExceptionType`, `Since`, `Until`, `PageSize` (default 25; 1 to `DeadLetterFilter.MaxPageSize`, 1000, or the query throws `ArgumentOutOfRangeException`), and `PageToken`. All filters are applied in SQL. `ReplayByTypeAsync` reads every page of its type before it replays the first entry.

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
| `Reference` | For a request that failed unexpectedly, the support reference its caller was answered with and the logged exception carries; null otherwise |
| `MessageId` | Original MessageContext.MessageId |
| `CausationId` | Original MessageContext.CausationId |
| `ConversationId` | Original MessageContext.ConversationId |
| `AttemptCount` | Number of times this message has been dead-lettered |
| `DeadLetteredAt` | Timestamp when the message was captured |

## Replay by transport

Replay re-publishes through the source transport's `RedeliverAsync`, which targets the service's inbound queue directly so any available instance picks the message up.

Every broker `RedeliverAsync` deliberately skips the base class's outbound header assembly: its metadata is the original correlation ids only, mapped onto the broker's native properties as any publish maps them, so a replayed message carries **no** `conduit-source-app` stamp. This is load-bearing: stamping the replaying host's `ApplicationName` would make the [self-published event filter](transports/index.md#self-published-events) drop the replayed event the moment it arrived back, silently voiding the replay. Do not "unify" replay onto the normal send path without addressing that.

| Transport | Redeliver mechanism |
|---|---|
| **Azure Service Bus** | Sends directly to the app queue (bypasses the topic). |
| **RabbitMQ** | Publishes to the app queue via the default exchange (routing key = queue name). |
| **Amazon SQS** | `SendMessage` directly to the app queue URL. |
| **ActiveMQ Artemis** | Sends by fully qualified name (`address::queue`) to the queue the app takes the type from: a command's shared (or pool) queue on the anycast address, an event's app (or pool) queue on the multicast address. Every queue filters to its own types, so the queue matters. |
| **HTTP** | POST to the dispatch/publish endpoint, with the current context's ids, not the original ones. |
| **SignalR** | Invokes the hub's `Dispatch`/`Publish` method, with the current context's ids, not the original ones. |

Only broker transports capture dead letters, so a dead-letter replay always goes through one of the first four; the HTTP and SignalR rows describe `RedeliverAsync` called directly.

## Why database-backed only (for now)

The DLQ has a single backing store: the Conduit database. We evaluated also wrapping each broker's *native* dead letter facility (ASB `/$DeadLetterQueue`, RabbitMQ dead-letter exchanges, SQS redrive, ActiveMQ DLA) as an alternative store, and decided against it **for now**. The deciding factors:

- **Non-uniform, fragile browsing.** Non-destructive browse needs a different mechanism per broker — ASB `PeekMessagesAsync` (clean), RabbitMQ via the Management HTTP API or `basic.get` + `nack(requeue)`, SQS via `ReceiveMessage` with `VisibilityTimeout=0`, ActiveMQ via a management `browse`. Several only offer approximate pagination.
- **Best-effort filtering.** Only a SQL store can apply arbitrary filters server-side. Broker-native browsing filters client-side over peeked pages.
- **No reliable counts.** Most brokers can't return a total count without consuming the queue.
- **Racy removal/replay.** Pulling one specific message out of a broker DLQ (receive-by-handle/sequence + ack) isn't transactional and races with concurrent browsers.
- **It duplicates the database store unevenly.** The database store already works with every transport (including HTTP and SignalR) and gives full query, filter, count, and replay. Broker-native would add per-transport topology and three operations (write/query/remove) × four brokers for a strictly worse, inconsistent browse experience.

This isn't a permanent exclusion — broker-native capture could be added later as an additive per-transport option if a strong use case appears (e.g. avoiding a database dependency entirely). Today the cost/inconsistency isn't worth it.

Note that the broker's own DLQ still functions as an infrastructure safety net: if you don't enable Conduit's DLQ, failed messages are abandoned and the broker redelivers (and dead-letters) per its own configuration — Conduit just doesn't browse or replay through it. RabbitMQ has no redelivery limit on a classic queue, so its transport dead-letters an abandoned message at once, to the `{system}-dead-letter` exchange and queue it declares (see [Failed messages](transports/rabbitmq.md#failed-messages)). SQS dead-letters only through a queue's redrive policy, so its transport gives every queue it consumes a dead-letter queue, `{queue}-dlq`, and a redrive policy to it (see [Failed messages](transports/amazon-sqs.md#failed-messages)).

## Limitations

- **Requires a database.** The DLQ shares the durability database. A stateless service with no database can't use it (use broker-native redelivery/DLQ at the infrastructure level instead).
- **Replay is fire-and-forget.** For request/response messages (commands with results, queries), the handler executes for side effects — there is no caller waiting for the response, and nothing receives it. `ReplayResult.Success` says the message was re-published, not that it was handled.
- **Replay does not guarantee idempotency.** If the original handler partially succeeded before failing, the replayed attempt is a new execution. Application-level idempotency is the handler's responsibility. The inbox does not stand in for it: the failed attempt released its record, so the replay runs (see [Interaction with inbox](#interaction-with-inbox)).
- **Refusals are not captured.** A request answered with a `ConduitException` (404, 409, 412, ...) is final: the caller has its answer, and nothing is stored to replay. A refused `[FireAndForget]` command is just as final, although no one hears the refusal but the log.
- **No auto-replay.** Replay is always triggered explicitly via `IDeadLetters`. Automatic retry-from-DLQ (with backoff) is a potential future feature.
