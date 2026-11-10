# RabbitMQ

- [Overview](#overview)
- [Setup](#setup)
- [Options](#options)
  - [Loading from configuration](#loading-from-configuration)
- [Topology](#topology)
  - [Unicast correctness](#unicast-correctness)
- [Developer and test modes](#developer-and-test-modes)
- [Request/response](#requestresponse)
  - [Connection recovery](#connection-recovery)
- [Publisher confirms](#publisher-confirms)
- [Failed messages](#failed-messages)
- [Auto-discovery](#auto-discovery)
- [Wire format](#wire-format)
- [Receiver pools](#receiver-pools)

## Overview

The RabbitMQ transport provides durable, broker-based messaging over AMQP. All message types (commands, queries, events) flow through a single topic exchange with routing keys. Commands and queries use request/response with correlation IDs and a transient reply queue per transport instance. Every publish waits for the broker's confirm, and a message whose listener fails is dead-lettered (by Conduit's dead-letter store when there is one, otherwise by the broker) rather than redelivered forever. The transport runs as an `IHostedService` that manages topology, consumers, and reply correlation.

## Setup

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("orders", endpoint =>
    {
        endpoint.UseRabbitMQ("amqp://guest:guest@localhost:5672/");
        endpoint.RouteNamespace<Orders.Commands.CreateOrder>();
    });
});
```

`UseRabbitMQ` registers the transport as both `IConduitTransport` and `IHostedService`. The transport starts when the host starts.

## Options

```csharp
endpoint.UseRabbitMQ(connectionString, options =>
{
    options.DefaultConcurrency = 10;       // default: 10
});
```

| Property | Default | Description |
|---|---|---|
| `ConnectionString` | *(required)* | AMQP connection string, e.g. `"amqp://guest:guest@localhost:5672/"` |
| `DefaultConcurrency` | `10` | How many handlers each default consumer runs at once: the consumer channel's prefetch count (`BasicQos`) and its consumer dispatch concurrency. Must be between 1 and 65535. |
| `ResponseTimeout` | `60s` | Timeout for request/response operations |

`UseRabbitMQ` throws `ArgumentOutOfRangeException` for a `DefaultConcurrency` or pool `Concurrency` outside 1..65535: both settings are AMQP shorts on the wire, and a value outside that range used to be cast silently (`0` meaning an unlimited prefetch, `65536` wrapping to `0`).

Exchange and queue names are controlled by `SystemName` and `TopologyMode` on `ConduitOptions` (see [Developer and test modes](#developer-and-test-modes)).

### Loading from configuration

Every property above also binds from `Conduit:Endpoints:{endpointName}`, so `UseRabbitMQ()` can run with no arguments:

```json
{
  "Conduit": {
    "Endpoints": {
      "orders": {
        "ConnectionString": "amqp://guest:guest@rmq:5672/",
        "DefaultConcurrency": 20,
        "ResponseTimeout": "00:00:30"
      }
    }
  }
}
```

```csharp
conduit.AddEndpoint("orders", endpoint => endpoint.UseRabbitMQ());
```

Configuration binds first; an explicit `UseRabbitMQ(connectionString)` argument or a `configure` callback overrides it. See [Loading from IConfiguration](../configuration-reference.md#loading-from-iconfiguration) for the full precedence rule, the pool-merge behavior, and the complete key reference.

## Topology

The transport declares the following on startup (idempotent — safe to run on every startup):

```
Exchange: {systemName} (type: topic)
Exchange: {systemName}-dead-letter (type: fanout)
└── Queue: {systemName}-dead-letter
Queue: {systemName}-{appName}                     (events, one copy per application)
├── Binding: routing key = {FullTypeName} (per subscribed event type)
Queue: {systemName}-{TypeName-reversed}           (per handled command/query type, shared)
├── Binding: routing key = {FullTypeName}
Queue: {systemName}-{appName}-{poolName}          (a pool's events)
Queue: {systemName}-{TypeName-reversed}-{poolName} (a pool's commands/queries)
Reply queue: {systemName}-{appName}-replies-{instanceId} (exclusive, auto-delete, one per transport instance)
```

All message types (commands, queries, events) flow through a single topic exchange. There is no direct exchange. Each handled type gets a queue binding with the full type name as the routing key — including event types this application publishes itself, so its own broadcasts route back to its own queue. See [Self-published events](index.md#self-published-events) for how the inbound side avoids running that application's own listeners twice. Reply messages bypass the exchange and go directly to the sender's reply queue.

Every queue a consumer reads (all of them but the reply queue and the dead-letter queue itself) is declared with `x-dead-letter-exchange: {systemName}-dead-letter`, so a message its consumer rejects goes to the system's dead-letter queue: see [Failed messages](#failed-messages). One dead-letter exchange and queue serve every application in the system.

All declarations are idempotent, as long as the arguments match. A durable queue declared before this transport added `x-dead-letter-exchange` (or declared by hand with other arguments) makes the broker refuse the declaration with `PRECONDITION_FAILED` (406, "inequivalent arg"), and the transport fails to start: delete the queue, or re-declare it with the arguments above. An application whose `ApplicationName` is `dead-letter` would name its event queue `{systemName}-dead-letter`, the dead-letter queue's name, so its startup fails with the same `PRECONDITION_FAILED`: give the application another name (and delete the queue if one was already declared under it).

Every resource name is sanitized to `[A-Za-z0-9_-]`, any other character becoming `-`. The default `ApplicationName` is the entry assembly's name, so an application named `Conduit.Sample.Orders` gets the queue `conduit-Conduit-Sample-Orders`, not `conduit-Conduit.Sample.Orders`.

### Unicast correctness

RabbitMQ does not have a built-in unicast subscription concept like Azure Service Bus. Unicast correctness for commands and queries relies on only one app binding a given type's routing key. If two apps bind the same command type, RabbitMQ will round-robin between them, which is incorrect for request/response semantics. Ensure each command/query type is handled by exactly one service.

## Developer and test modes

`TopologyMode` on `ConduitOptions` controls resource naming and durability:

| Mode | Prefix | Durability | Auto-delete |
|---|---|---|---|
| `Production` | None | Durable | Never |
| `Developer` | `{TopologyPrefix}` (default: username) | Non-durable | Auto-delete |
| `Test` | `{TopologyPrefix}` (default: `test-{random}`) | Non-durable | Auto-delete |

Developer mode gives each developer isolated broker resources (e.g. exchange `bpage-conduit` instead of `conduit`). Test mode creates fully isolated, short-lived resources for integration tests.

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.TopologyMode = TopologyMode.Developer;
    conduit.TopologyPrefix = "myprefix";  // optional, defaults to Environment.UserName

    conduit.AddEndpoint("orders", endpoint =>
        endpoint.UseRabbitMQ(connectionString));
});
```

## Request/response

When the generated mediator routes a command to a RabbitMQ endpoint:

1. The transport publishes the command to the topic exchange with the full type name as the routing key. The message includes `ReplyTo` and `CorrelationId` properties.
2. The queue binding routes the message to the handler's app queue.
3. The receiving service processes the command and publishes a reply directly to the sender's reply queue with the matching correlation ID.
4. The sender matches the reply by correlation ID and deserializes the `DispatchResult`.

Replies time out after `ResponseTimeout` (default 60 seconds). Reply messages bypass the topic exchange.

A request with no reply within `ResponseTimeout` fails with a `TimeoutException` (`No reply to '{type}' from endpoint '{endpoint}' within {timeout}.`), the same on every broker transport. It is never an `OperationCanceledException`, which only the caller's own cancellation token produces, so a `catch (OperationCanceledException)` around a dispatch cannot swallow a remote that did not answer. A request still waiting when the transport stops fails with an `InvalidOperationException`.

Each transport instance has its own reply queue, `{systemName}-{appName}-replies-{instanceId}`: exclusive to the instance's connection, so no other client on the vhost can consume its replies, and deleted with that connection. Replies are published on a channel of their own, apart from the outbound publishes, so a channel error on one cannot close the other.

If another connection already holds an exclusive queue under that name, the broker refuses the declaration with `RESOURCE_LOCKED` (405). The transport then takes a fresh instance id, declares `{systemName}-{appName}-replies-{newInstanceId}` instead, and addresses every request from then on to it, logging a Warning (`ReplyQueueLocked`, EventId 107) that names the old and the new queue. A request still awaiting a reply on the old name fails at `ResponseTimeout`, as any lost reply does. The usual holder is the instance's own previous connection after a network drop, which the broker keeps until it misses its heartbeats.

### Connection recovery

The RabbitMQ client recovers a lost connection on its own (a broker restart, a network drop): it reconnects every few seconds, then re-declares the exchanges, queues and bindings the connection declared and restarts its consumers. The reply queue is the exception: the transport leaves it out of the client's recovery and re-declares it itself once the rest has recovered, before any later recovery handler runs, with the fallback above. The client would re-declare the exclusive queue too, but a `RESOURCE_LOCKED` there abandons the whole recovery attempt, and it would keep retrying for as long as the broker held the old connection. The reply queue is named by the transport rather than by the broker so that it comes back under the same name: a broker-named queue got a new name on recovery while every request still carried the old one in `ReplyTo`, so no reply reached the instance again until it restarted. A request whose reply was lost with the connection still fails at `ResponseTimeout`. If re-declaring the reply queue fails for any other reason, the transport logs an Error (`ReplyQueueNotRecovered`, EventId 108), and requests time out until the host restarts.

## Publisher confirms

Every publish waits for the broker's confirm: a request, an event, a dead-letter replay and a reply are reported sent only once RabbitMQ has acknowledged them, and a message the broker refuses throws (a `PublishException` from the client) instead of vanishing. An event published to the exchange with no queue bound to its type is still confirmed: RabbitMQ acknowledges a message it routed nowhere, and no subscriber is not an error. A reply is confirmed before its request is acknowledged.

## Failed messages

A failed message is never requeued in a loop. Where one that must be kept goes depends on whether the host registered Conduit's [dead-letter queue](../dead-letter-queue.md):

- **With Conduit's dead-letter store** (`AddDurability(d => d.AddDeadLetterQueue())`), Conduit captures the failed message in its database, with the exception and the message context, and acknowledges it on the broker. Nothing reaches the broker's dead-letter queue, and the message is browsed and replayed through `IDeadLetters`.
- **Without one**, the transport rejects the message (`BasicNack`, `requeue: false`), and its queue's dead-letter exchange routes it to `{systemName}-dead-letter` with its properties and headers intact, plus the broker's `x-death` and `x-first-death-*` headers (`x-first-death-reason: rejected`, `x-first-death-queue`: the queue it came from). It is the broker's to inspect, and to move back (with the management UI or a shovel) once the cause is fixed. Today this is a failed event listener (`EventListenerException`), and a routed `[FireAndForget]` command whose dispatch itself threw (its handler's own failure is logged and acknowledged): what Conduit's store would otherwise have captured. A message Conduit settles without a store (an undispatchable body, a refused event, a request whose failure was already answered to its caller) is acknowledged, as on every broker.

A failure used to be requeued (`requeue: true`), so a listener that always failed received its event again at once, forever. A message delivered after the [drain](index.md#stopping-a-host-with-work-in-flight) began is not a failure: it was never dispatched, and it is requeued (`requeue: true`) for another instance to take, not dead-lettered.

The dead-letter queue is never auto-delete, so a consumer that attaches to it and detaches again does not take the dead letters with it. It is durable in Production. In Developer mode it is non-durable: one per developer prefix, kept until the broker restarts. In Test mode, where every run has its own prefix, it is non-durable and expires (`x-expires`) once unused for 5 minutes, and its exchange goes with it.

## Auto-discovery

The transport reads `ConduitTypeRegistry` — populated at compile time by the source generator — to learn which types this application handles:

- `IEventListener<T>` — binds the app queue to the topic exchange for each event type
- `ICommandHandler<T>`, `ICommandHandler<T,R>`, `IQueryHandler<T,R>` — binds the app queue for each command/query type

A listener registered at runtime counts too: `AddJobs` (an event trigger's event), `AddAuditListener`
(`AuditBatch`) and a `ConfigureConduitRegistrations` `HandleEvent` override activate their event in the
registry (`ConduitTypeRegistry.ActivateEvent`), so it is subscribed like an event whose listener the
generator found.

No manual binding configuration is needed. When a message is published, the transport sends it to the topic exchange with the full type name as the routing key. RabbitMQ routes the message to all queues with matching bindings.

## Wire format

Messages use JSON serialization with type information in AMQP properties:

| Property | Value |
|---|---|
| Body | JSON-serialized message payload |
| `Type` | Full type name (e.g. `"Orders.Commands.CreateOrder"`) |
| `ContentType` | `"application/json"` |
| `MessageId` | From `MessageContext` or auto-generated |
| `CorrelationId` | Request/response correlation (requests only) |
| `ReplyTo` | Reply queue name (requests only) |
| Header `CausationId` | From `MessageContext.CausationId` — historical name, kept for existing consumers |
| Header `ConversationId` | From `MessageContext.ConversationId` — historical name, kept for existing consumers |
| Header `conduit-message-id`, `conduit-causation-id`, `conduit-conversation-id` | Canonical forms of the same three values, written alongside the historical names above |
| Header `conduit-source-app` | The publishing application's `ApplicationName`, used by the [self-published event filter](index.md#self-published-events) (omitted when `ApplicationName` is unset) |

## Receiver pools

For concurrency isolation, configure named receiver pools:

```csharp
endpoint.UseRabbitMQ(connectionString, options =>
{
    options.DefaultConcurrency = 20;

    options.AddPool("imports", pool =>
    {
        pool.Concurrency = 2;
        pool.Handle("Catalog.Commands.BulkImportCommand");
    });
});
```

This routes `BulkImportCommand` to a dedicated queue of the `imports` pool (`{systemName}-BulkImportCommand-Commands-Catalog-imports`; a pool's events share `{systemName}-{appName}-imports`) whose consumer runs at most 2 handlers at once. Everything else uses the default consumers, 20 handlers at once each.

A consumer's concurrency is two settings on its channel: the prefetch count (`BasicQos`), how many unacknowledged messages RabbitMQ delivers to it at once, and the client's consumer dispatch concurrency, how many of those it hands to handlers at once. Both take the same value, so up to that many handlers really run in parallel. Lower values limit throughput but keep a slow consumer from buffering too many messages. Like `DefaultConcurrency`, `pool.Concurrency` must be between 1 and 65535.

`AddPool` is get-or-create by name: a pool can be declared in configuration (`Pools` array under the
endpoint's section) and configured again in code on the same name, and the two merge into one pool
rather than producing a duplicate. Name matching is case-insensitive, and because configuration
binds first, the configured spelling of `Name` is the one that survives into queue naming — a code
callback's `AddPool("imports", …)` merges into a configured `"Imports"` without renaming it.
`pool.Handle(...)` de-duplicates, so the merged pool's message
types are the union of what configuration listed and what code added:

```jsonc
{ "Name": "imports", "Concurrency": 2, "MessageTypes": [ "Catalog.Commands.BulkImportCommand" ] }
```
```csharp
options.AddPool("imports", pool => pool.Handle<ReindexCatalogCommand>());
// -> one pool "imports", Concurrency 2, handling
//    [Catalog.Commands.BulkImportCommand, Catalog.Commands.ReindexCatalogCommand]
```

---

Previous: [Azure Service Bus](azure-service-bus.md) | [Home](../../README.md) | Next: [Amazon SQS](amazon-sqs.md)
