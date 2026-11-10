# RabbitMQ

- [Overview](#overview)
- [Setup](#setup)
- [Options and configuration keys](#options-and-configuration-keys)
  - [Loading from configuration](#loading-from-configuration)
- [Topology](#topology)
  - [Competing consumers](#competing-consumers)
  - [Auto-discovery](#auto-discovery)
- [Developer and Test modes](#developer-and-test-modes)
- [Request/response](#requestresponse)
  - [Connection recovery](#connection-recovery)
- [Failure handling](#failure-handling)
  - [Publisher confirms](#publisher-confirms)
  - [Stopping](#stopping)
- [Wire format](#wire-format)
- [Receiver pools](#receiver-pools)
- [Limitations](#limitations)

## Overview

The RabbitMQ transport provides broker-based messaging over AMQP 0-9-1. All message types (commands, queries, events) flow through a single topic exchange, with the message's full type name as the routing key. Each command or query type has a queue of its own, and each application has one queue for the events it listens for. Commands and queries use request/response with correlation IDs and a reply queue per transport instance. Every publish waits for the broker's confirm, and a message whose listener fails is dead-lettered (by Conduit's dead-letter store when there is one, otherwise by the broker) rather than redelivered forever. The transport runs as an `IHostedService` that manages topology, consumers, and reply correlation.

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

## Options and configuration keys

```csharp
endpoint.UseRabbitMQ(connectionString, options =>
{
    options.DefaultConcurrency = 20;
    options.ResponseTimeout = TimeSpan.FromSeconds(30);
});
```

`RabbitMQOptions`:

| Property | Default | Description |
|---|---|---|
| `ConnectionString` | *(required)* | AMQP URI, e.g. `"amqp://guest:guest@localhost:5672/"`. `UseRabbitMQ` throws `InvalidOperationException` when neither code nor configuration supplies one. |
| `DefaultConcurrency` | `10` | How many handlers each queue's consumer runs at once: its channel's prefetch count (`BasicQos`) and its consumer dispatch concurrency. Every queue the application consumes has a consumer of its own (see [Topology](#topology)). Between 1 and 65535. |
| `ResponseTimeout` | `00:01:00` | How long a request waits for its reply, publish included (see [Request/response](#requestresponse)). Positive and finite. |
| `Pools` | empty | Named receiver pools, added with `AddPool` (see [Receiver pools](#receiver-pools)). |
| `EndpointName` | the endpoint's name | Read-only: `UseRabbitMQ` sets it from `AddEndpoint`, never from configuration. |

`UseRabbitMQ` throws `ArgumentOutOfRangeException` for a `DefaultConcurrency` or pool `Concurrency` outside 1..65535: both settings are AMQP shorts on the wire. It throws `InvalidOperationException` for a `ResponseTimeout` that is not positive and finite (see [Request timeouts](index.md#request-timeouts)).

Exchange and queue names are controlled by `SystemName`, `ApplicationName` and `TopologyMode` on `ConduitOptions` (see [Developer and Test modes](#developer-and-test-modes)).

### Loading from configuration

Every property above but `EndpointName` also binds from `Conduit:Endpoints:{endpointName}`, under the same name, so `UseRabbitMQ()` can run with no arguments:

```json
{
  "Conduit": {
    "Endpoints": {
      "orders": {
        "ConnectionString": "amqp://guest:guest@rmq:5672/",
        "DefaultConcurrency": 20,
        "ResponseTimeout": "00:00:30",
        "Pools": [
          { "Name": "imports", "Concurrency": 2, "MessageTypes": [ "Catalog.Commands.BulkImportCommand" ] }
        ]
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

The transport declares the following on startup (idempotent: safe to run on every startup):

```
Exchange: {systemName}                                  topic: every command, query and event
Exchange: {systemName}-dead-letter                      fanout
└── Queue: {systemName}-dead-letter                     the system's dead letters
Queue: {systemName}-{appName}                           the events the application listens for
└── Binding: routing key {FullTypeName}                 one per event type, minus pooled ones
Queue: {systemName}-{ReversedTypeName}                  one per command or query type, shared by every application that handles it
└── Binding: routing key {FullTypeName}
Queue: {systemName}-{appName}-{poolName}                a pool's events (only when the pool takes an event type)
Queue: {systemName}-{ReversedTypeName}-{poolName}       one per pooled command or query type
Queue: {systemName}-{appName}-replies-{instanceId}      exclusive, auto-delete: one per transport instance
```

`{systemName}` is `ConduitOptions.SystemName` (`conduit` by default), prefixed with `{TopologyPrefix}-` in Developer and Test modes. `{ReversedTypeName}` is the full type name with its segments reversed and joined by `-`: `Orders.Commands.CreateOrder` gets the queue `conduit-CreateOrder-Commands-Orders`.

All message types (commands, queries, events) flow through a single topic exchange. There is no direct exchange. Each queue is bound with the full type names it takes as routing keys, including event types this application publishes itself, so its own broadcasts route back to its own queue when it also listens for them. See [Self-published events](index.md#self-published-events) for how the inbound side avoids running that application's own listeners twice. Reply messages bypass the exchange and go directly to the sender's reply queue.

Every queue a consumer reads (all of them but the reply queue and the dead-letter queue itself) is declared with `x-dead-letter-exchange: {systemName}-dead-letter`, so a message its consumer rejects goes to the system's dead-letter queue: see [Failure handling](#failure-handling). One dead-letter exchange and queue serve every application in the system.

All declarations are idempotent, as long as the arguments match. A durable queue declared without `x-dead-letter-exchange` (by an earlier build, or by hand with other arguments) makes the broker refuse the declaration with `PRECONDITION_FAILED` (406, "inequivalent arg"), and the transport fails to start: delete the queue, or re-declare it with the arguments above. An application whose `ApplicationName` is `dead-letter` would name its event queue `{systemName}-dead-letter`, the dead-letter queue's name, so its startup fails with the same `PRECONDITION_FAILED`: give the application another name (and delete the queue if one was already declared under it).

A `ProducerOnly` host declares only the topic exchange and its reply queue: no application, command, pool or dead-letter queue, and no consumer.

Every resource name is sanitized to `[A-Za-z0-9_-]`, any other character becoming `-`. The default `ApplicationName` is the entry assembly's name, so an application named `Conduit.Sample.Orders` gets the queue `conduit-Conduit-Sample-Orders`, not `conduit-Conduit.Sample.Orders`.

### Competing consumers

The instances of one application share its event queue, so each event reaches one instance of every application that listens for it.

A command or query queue is named after its type alone, so it is shared by every application that handles the type, not only by one application's instances. Two applications that handle the same command are competing consumers on one queue: each message reaches exactly one of them, whichever consumer RabbitMQ hands it to. A request still gets one reply, but which application's handler produced it is arbitrary, so handle each command or query type in one application.

A pooled type's queue carries the pool's name. An application that pools a command and another that does not (or pools it under another name) bind two queues to the same routing key, and RabbitMQ copies each message to both, so both applications handle it. Pool a shared type under the same name everywhere, or nowhere.

### Auto-discovery

The transport reads `ConduitTypeRegistry`, which the source generator fills at compile time, to learn which types this application takes. There is no runtime scan of the container:

- An event type with an `IEventListener<T>` is bound to the application's event queue (or to its pool's event queue).
- A type with an `ICommandHandler<T>`, `ICommandHandler<T,R>` or `IQueryHandler<T,R>` gets its shared queue (or its pool queue), declared and bound.

A listener registered at runtime counts too: `AddJobs` (an event trigger's event), `AddAuditListener`
(`AuditBatch`) and a `ConfigureConduitRegistrations` `HandleEvent` override activate their event in the
registry (`ConduitTypeRegistry.ActivateEvent`), so it is subscribed like an event whose listener the
generator found.

No manual binding configuration is needed. When a message is published, the transport sends it to the topic exchange with the full type name as the routing key, and RabbitMQ routes it to every queue bound to that key.

## Developer and Test modes

`TopologyMode` on `ConduitOptions` controls resource naming and lifetime:

| Mode | Prefix | Durable | Cleanup |
|---|---|---|---|
| `Production` | None | Yes: exchanges and queues (but see [Limitations](#limitations) on messages) | None |
| `Developer` | `TopologyPrefix`, by default `Environment.UserName` | No: a broker restart deletes them | Auto-delete: a queue goes when its last consumer leaves, an exchange when its last binding goes |
| `Test` | `TopologyPrefix`, by default `test-` and 8 random hex characters | No | As Developer, and the dead-letter queue expires once unused for 5 minutes |

Developer mode gives each developer isolated broker resources (e.g. exchange `alice-conduit` instead of `conduit`). Test mode creates fully isolated, short-lived resources for integration tests. The dead-letter queue is never auto-delete (see [Failure handling](#failure-handling)).

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.TopologyMode = TopologyMode.Developer;
    conduit.TopologyPrefix = "alice";  // optional, defaults to Environment.UserName

    conduit.AddEndpoint("orders", endpoint =>
        endpoint.UseRabbitMQ(connectionString));
});
```

## Request/response

When the generated mediator routes a command to a RabbitMQ endpoint:

1. The transport publishes the command to the topic exchange with the full type name as the routing key. The message carries `ReplyTo` and `CorrelationId` properties.
2. The binding routes the message to the type's shared queue, where one consumer receives it.
3. The receiving service processes the command and publishes a reply directly to the sender's reply queue with the matching correlation ID.
4. The sender matches the reply by correlation ID and deserializes the `DispatchResult`. A failed result throws `RemoteDispatchException` on the caller, carrying the remote error's code and, for an unexpected failure, its support reference.

A request with no reply within `ResponseTimeout` (default 60 seconds) fails with a `TimeoutException` (`No reply to '{type}' from endpoint '{endpoint}' within {timeout}.`), the same on every broker transport. The timeout covers the publish (and its [confirm](#publisher-confirms)) and the wait for the reply together, so a request whose publish the broker has not confirmed by then fails the same way (a broker under a memory or disk alarm stops reading the connection). It is never an `OperationCanceledException`, which only the caller's own cancellation token produces, so a `catch (OperationCanceledException)` around a dispatch cannot swallow a remote that did not answer. A request still waiting when the transport stops fails with an `InvalidOperationException`.

Each transport instance has its own reply queue, `{systemName}-{appName}-replies-{instanceId}`: exclusive to the instance's connection, so no other client on the vhost can consume its replies, and deleted with that connection. Replies are published on a channel of their own, apart from the outbound publishes, so a channel error on one cannot close the other.

If another connection already holds an exclusive queue under that name, the broker refuses the declaration with `RESOURCE_LOCKED` (405). The transport then takes a fresh instance id, declares `{systemName}-{appName}-replies-{newInstanceId}` instead, and addresses every request from then on to it, logging a Warning (`ReplyQueueLocked`, EventId 107) that names the old and the new queue. A request still awaiting a reply on the old name fails at `ResponseTimeout`, as any lost reply does. The usual holder is the instance's own previous connection after a network drop, which the broker keeps until it misses its heartbeats.

### Connection recovery

The RabbitMQ client recovers a lost connection on its own (a broker restart, a network drop): it reconnects every few seconds, then re-declares the exchanges, queues and bindings the connection declared and restarts its consumers. The reply queue is the exception: the transport leaves it out of the client's recovery and re-declares it itself once the rest has recovered, before any later recovery handler runs, with the fallback above. The client would re-declare the exclusive queue too, but a `RESOURCE_LOCKED` there abandons the whole recovery attempt, and it would keep retrying for as long as the broker held the old connection. The transport, not the broker, names the reply queue, so it comes back under the name every request carries in `ReplyTo`.

A request whose reply was lost with the connection still fails at `ResponseTimeout`, and so does one published after the client has recovered its channels but before the transport has re-declared the reply queue: its reply is routed to a queue that does not exist yet and is dropped, as any lost reply is. If re-declaring the reply queue fails for any other reason, the transport logs an Error (`ReplyQueueNotRecovered`, EventId 108), and requests time out until the host restarts.

## Failure handling

<a id="failed-messages"></a>

A broker transport settles each message once, by what became of its dispatch, and never retries it itself: retry happens inside the dispatch ([dispatch retry](../dispatch-retry.md), or a [pipeline behavior](../pipeline-behaviors.md)). The rules every broker shares are in [Failed messages](index.md#failed-messages). On RabbitMQ:

- **A request whose handler throws** is answered: its caller receives the failure as its reply (`InternalError` with a support reference, or the refusal the handler threw), and the request is acknowledged. An unexpected failure is also captured when the host has Conduit's dead-letter store.
- **An event whose listener throws, or a `[FireAndForget]` command whose handler fails unexpectedly**, has no caller to tell. Where it goes depends on whether the host registered Conduit's [dead-letter queue](../dead-letter-queue.md):
  - **With Conduit's dead-letter store** (`AddDurability(d => d.AddDeadLetterQueue())`), Conduit captures the failed message in its database, with the exception and the message context, and acknowledges it on the broker. Nothing reaches the broker's dead-letter queue, and the message is browsed and replayed through `IDeadLetters`.
  - **Without one**, the transport rejects the message (`BasicNack`, `requeue: false`), and its queue's dead-letter exchange routes it to `{systemName}-dead-letter` with its properties and headers intact, plus the broker's `x-death` and `x-first-death-*` headers (`x-first-death-reason: rejected`, `x-first-death-queue`: the queue it came from). The handler ran once: RabbitMQ does not redeliver it. It is the broker's to inspect, and to move back (with the management UI or a shovel) once the cause is fixed.
- **A message this host cannot read, or one it refuses** (an undispatchable body, a refused event or fire-and-forget command) is acknowledged, and captured first when it is unreadable and the host has the store, as on every broker.

A message delivered after the [drain](index.md#stopping-a-host-with-work-in-flight) began is not a failure: it was never dispatched, and it is requeued (`requeue: true`) for another instance to take, not dead-lettered.

The dead-letter queue is never auto-delete, so a consumer that attaches to it and detaches again does not take the dead letters with it. It is durable in Production. In Developer mode it is non-durable: one per developer prefix, kept until the broker restarts. In Test mode, where every run has its own prefix, it is non-durable and expires (`x-expires`) once unused for 5 minutes. "Unused" is RabbitMQ's: no consumer, no `basic.get`, and no re-declaration (each host start re-declares it, renewing the lease); dead letters arriving do not count as use. So a Test-mode host that runs longer than 5 minutes after the last start in its system, with nothing reading the dead-letter queue, loses it and whatever it holds, and the auto-delete exchange goes with its last binding (later dead letters are then dropped).

### Publisher confirms

Every publish waits for the broker's confirm: a request, an event, a dead-letter replay and a reply are reported sent only once RabbitMQ has acknowledged them, and a message the broker refuses throws (a `PublishException` from the client) instead of vanishing. An event published to the exchange with no queue bound to its type is still confirmed: RabbitMQ acknowledges a message it routed nowhere, and no subscriber is not an error. A reply is confirmed before its request is acknowledged. A request's confirm counts against its `ResponseTimeout`; a reply's confirm is bounded only by the drain deadline when the host stops.

### Stopping

When the host stops, the transport follows the [shared stop sequence](index.md#stopping-a-host-with-work-in-flight). It cancels its consumers (`BasicCancel`) but keeps their channels open, so a handler still running acknowledges its message on the channel it arrived on, and it keeps the reply consumer running, since a handler being drained may await a reply. A message delivered after the drain began is requeued undispatched, as above. Once the handlers have drained, the channels close, and RabbitMQ requeues anything still unacknowledged on them.

## Wire format

Messages use JSON serialization (`ConduitRuntime.JsonOptions`) with type information in AMQP properties:

| Property | Value |
|---|---|
| Body | JSON-serialized message payload |
| `Type` | Full type name (e.g. `"Orders.Commands.CreateOrder"`) |
| `ContentType` | `"application/json"` |
| `MessageId` | From `MessageContext` or auto-generated |
| `CorrelationId` | Request/response correlation (requests and their replies only) |
| `ReplyTo` | Reply queue name (requests only) |
| `DeliveryMode` | Not set: the message is transient (see [Limitations](#limitations)) |
| Header `CausationId` | From `MessageContext.CausationId` (historical name, kept for existing consumers) |
| Header `ConversationId` | From `MessageContext.ConversationId` (historical name, kept for existing consumers) |
| Header `conduit-message-id`, `conduit-causation-id`, `conduit-conversation-id` | Canonical forms of the same three values, written alongside the historical names above |
| Header `traceparent`, `tracestate` | W3C trace context, when the publish runs inside a trace |
| Header `conduit-source-app` | The publishing application's `ApplicationName`, used by the [self-published event filter](index.md#self-published-events) (omitted when `ApplicationName` is unset) |

A reply's body is the serialized `DispatchResult`, with the request's `CorrelationId`.

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

This routes `BulkImportCommand` to a queue of the `imports` pool, `{systemName}-BulkImportCommand-Commands-Catalog-imports`, whose consumer runs at most 2 handlers at once. Everything else uses the default consumers, 20 handlers at once each.

A pool gives each command or query type it takes a queue of its own, `{systemName}-{ReversedTypeName}-{poolName}`, and puts the event types it takes on one queue, `{systemName}-{appName}-{poolName}`, instead of the application's event queue. Each pool queue has a consumer of its own at the pool's `Concurrency` (default `1`), so a pool that takes three command types runs up to three times its `Concurrency`.

A consumer's concurrency is two settings on its channel: the prefetch count (`BasicQos`), how many unacknowledged messages RabbitMQ delivers to it at once, and the client's consumer dispatch concurrency, how many of those it hands to handlers at once. Both take the same value, so up to that many handlers really run in parallel. Lower values limit throughput but keep a slow consumer from buffering too many messages. Like `DefaultConcurrency`, `pool.Concurrency` must be between 1 and 65535.

A pool can be declared in configuration (the endpoint's `Pools` array) and in code under the same name: the two merge into one pool (see [Pool merging](../configuration-reference.md#pool-merging)). The names `replies` and `dead-letter` are reserved.

## Limitations

- **Messages are transient.** The transport publishes without the persistent delivery mode, so a broker restart drops the messages still in a queue, durable Production queues included. Only the queues and exchanges survive.
- **No broker redelivery.** A failed event or fire-and-forget command is rejected to the dead-letter queue at once (or captured by Conduit's store): RabbitMQ never runs its handler a second time. Retry it inside the dispatch.
- **Command and query queues are shared by type.** Two applications that handle the same type compete for its messages ([Competing consumers](#competing-consumers)).
- **One dead-letter queue per system.** Every application's broker-side dead letters land in `{systemName}-dead-letter`, which nothing consumes; moving them back is a job for the management UI or a shovel.
- **Classic queues with fixed arguments.** A queue declared beforehand with other arguments (a quorum queue, another dead-letter exchange) fails the start with `PRECONDITION_FAILED`.
- **A lost reply is a timeout.** A request whose reply was lost with a connection fails at `ResponseTimeout`, not sooner.

---

Previous: [Transports](index.md) | [Home](../../README.md) | Next: [Azure Service Bus](azure-service-bus.md)
