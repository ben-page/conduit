# ActiveMQ

- [Overview](#overview)
- [Setup](#setup)
- [Options](#options)
  - [Loading from configuration](#loading-from-configuration)
- [Topology](#topology)
  - [Anycast and multicast](#anycast-and-multicast)
- [Developer and test modes](#developer-and-test-modes)
- [Request/response](#requestresponse)
- [Connection recovery](#connection-recovery)
- [Failed messages](#failed-messages)
- [Stopping](#stopping)
- [Auto-discovery](#auto-discovery)
- [Wire format](#wire-format)
- [Receiver pools](#receiver-pools)

## Overview

The ActiveMQ transport provides durable, broker-based messaging over **AMQP 1.0** against **ActiveMQ Artemis**, using `AMQPNetLite.Core`. Artemis separates the *address* a message is sent to from the *queues* bound to it, and Conduit uses that split directly: one anycast address carries commands and queries, one multicast address carries events. Commands and queries use request/response over a per-connection temporary reply queue. The transport runs as an `IHostedService` that manages topology, receiver links, reply correlation, and reconnecting when the connection to the broker is lost.

This transport targets **Artemis** specifically — topology is created through the Artemis management address, which the older ActiveMQ "Classic" broker does not provide.

## Setup

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("orders", endpoint =>
    {
        endpoint.UseActiveMQ("amqp://localhost:5672");
        endpoint.RouteNamespace<Orders.Commands.CreateOrder>();
    });
});
```

`UseActiveMQ` registers the transport as both `IConduitTransport` and `IHostedService`. The transport starts when the host starts.

## Options

```csharp
endpoint.UseActiveMQ(connectionString, options =>
{
    options.Username = "artemis";
    options.Password = "artemis";
    options.DefaultConcurrency = 10;       // default: 10
});
```

| Property | Default | Description |
|---|---|---|
| `ConnectionString` | *(required)* | AMQP 1.0 connection string, e.g. `"amqp://localhost:5672"` |
| `Username` | `null` | SASL PLAIN username. Omit for an anonymous connection. |
| `Password` | `null` | SASL PLAIN password |
| `DefaultConcurrency` | `10` | Default consumer concurrency. For ActiveMQ this maps to the receiver link credit (prefetch count). |
| `ResponseTimeout` | `60s` | Timeout for request/response operations |

Address and queue names are controlled by `SystemName` and `TopologyMode` on `ConduitOptions` (see [Developer and test modes](#developer-and-test-modes)).

### Loading from configuration

Every property above also binds from `Conduit:Endpoints:{endpointName}`, so `UseActiveMQ()` can run with no arguments:

```json
{
  "Conduit": {
    "Endpoints": {
      "orders": {
        "ConnectionString": "amqp://artemis:5672",
        "Username": "artemis",
        "Password": "artemis",
        "DefaultConcurrency": 20,
        "ResponseTimeout": "00:00:30"
      }
    }
  }
}
```

```csharp
conduit.AddEndpoint("orders", endpoint => endpoint.UseActiveMQ());
```

Configuration binds first; an explicit `UseActiveMQ(connectionString)` argument or a `configure` callback overrides it. See [Loading from IConfiguration](../configuration-reference.md#loading-from-iconfiguration) for the full precedence rule, the pool-merge behavior, and the complete key reference.

## Topology

The transport creates the following on startup through the Artemis management address (`activemq.management`), idempotently:

```
Address: {systemName}-commands                     (ANYCAST)
├── Queue: {systemName}-{type}                     filter: JMSType='{FullTypeName}'         one per command/query type
├── Queue: {systemName}-{type}-{poolName}          filter: JMSType='{FullTypeName}'         one per pooled command/query type

Address: {systemName}-events                       (MULTICAST)
├── Queue: {systemName}-{appName}                  filter: JMSType IN ('{FullTypeName}', ...)  the app's events, minus pooled ones
├── Queue: {systemName}-{appName}-{poolName}       filter: JMSType IN ('{FullTypeName}', ...)  the pool's events

Reply queue: per connection, temporary (deleted with the connection)
```

`{type}` is the full type name with its segments reversed and joined by `-`: `Orders.Commands.CreateOrder` gets the queue `conduit-CreateOrder-Commands-Orders`. Every resource name is sanitized to `[A-Za-z0-9_-]`, any other character becoming `-`. The default `ApplicationName` is the entry assembly's name, so an application named `Conduit.Sample.Orders` gets the event queue `conduit-Conduit-Sample-Orders`, not `conduit-Conduit.Sample.Orders`.

### Anycast and multicast

Artemis routes by address type, which maps exactly onto Conduit's two delivery semantics:

- **Commands and queries** go to the **anycast** address, and so does a routed `[FireAndForget]` command, sent without a reply. Each command or query type gets one shared queue, named from the type, so exactly one consumer receives each message (unicast). Queues carry a `JMSType` filter so a message reaches only the queue for its own type.
- **Events** go to the **multicast** address. Each application gets its own queue bound to that address, so every subscribing application receives a copy.

Artemis hands every message on a multicast address to every queue bound to it, so each event queue carries a filter too: the application's queue takes the event types the application listens for, except the ones a [receiver pool](#receiver-pools) takes, and a pool's event queue takes the pool's. An application's queue never accumulates the event types other applications publish, and a pooled event reaches the application once, on the pool's queue. When an event queue already exists, the transport reads its filter at each start and reconnection and, if it differs from this release's types, sets it (`updateQueue`), so an event type a release adds reaches it. The update leaves the queue's consumers attached, so the peers already running keep receiving, and take the added types too. With several instances of one application running different releases, the filter is the one the last to start set.

Because the broker enforces the distinction, unicast correctness does not depend on convention here — unlike RabbitMQ, where two applications binding the same command type silently round-robin between them.

## Developer and test modes

`TopologyMode` on `ConduitOptions` controls resource naming and lifetime:

| Mode | Prefix | Durability | Queue auto-delete |
|---|---|---|---|
| `Production` | None | Durable | Never |
| `Developer` | `{TopologyPrefix}` (default: username) | Non-durable | 4 hours after its last consumer left |
| `Test` | `{TopologyPrefix}` (default: `test-{random}`) | Non-durable | 5 minutes after its last consumer left |

Developer mode gives each developer isolated broker resources (e.g. address `alice-conduit-commands` instead of `conduit-commands`). Test mode creates fully isolated, short-lived resources for integration tests.

Each queue is created with Artemis's own auto-delete settings (`autoDelete`, `autoDeleteDelay` of 4 hours or 5 minutes, and `autoDeleteMessageCount` -1), so no reaper is needed for the queues, unlike Amazon SQS. Artemis deletes a Developer or Test queue once it has had no consumer for the delay, whatever messages it still holds; it checks every `address-queue-scan-period` (30 seconds by default), so a queue can outlive its delay by that long. A queue that never had a consumer is never auto-deleted, but every queue the transport creates gets one at once. A broker restart deletes the non-durable queues of both modes anyway.

The two addresses are not auto-deleted. Artemis deletes only addresses it created itself, and its management API, through which the transport creates them, takes no auto-delete setting for an address. So each prefix leaves its two addresses behind, empty and holding no messages: a pair per developer on a Developer broker, and a pair per Test-mode prefix on a broker the test runs share. An address created through the management API also survives a broker restart. Remove them with the Artemis console or CLI when that matters.

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.TopologyMode = TopologyMode.Developer;
    conduit.TopologyPrefix = "myprefix";  // optional, defaults to Environment.UserName

    conduit.AddEndpoint("orders", endpoint =>
        endpoint.UseActiveMQ(connectionString));
});
```

## Request/response

When the generated mediator routes a command to an ActiveMQ endpoint:

1. The transport sends the command to the anycast address with `ReplyTo` and `ReplyCorrelationId` application properties.
2. The `JMSType` filter routes it to that type's shared queue, where exactly one consumer receives it.
3. The handler runs and the reply is sent directly to the sender's reply queue with the matching correlation id.
4. The sender matches the reply by correlation id and deserializes the `DispatchResult`.

Each transport instance has its own **exclusive temporary reply queue**: a dynamic node the broker creates and names when the transport attaches its reply receiver, and deletes with the connection — so a reply reaches the instance that sent the request, and no reply queues are left behind. A [reconnection](#connection-recovery) gets a new one under a new name. The receiving side sends replies through one sender link per reply address, kept for the next reply to the same requester. At most 64 are kept, however many distinct reply addresses arrive: a new one beyond that evicts the least recently used link that no reply is being sent on, and adding one also evicts links unused for 5 minutes, since a requester that went away leaves its link attached. A link is never closed while a reply is being sent on it: a reply whose send fails is not retried (the request is acknowledged all the same), so that would lose it. When every kept link has a reply in flight, the reply gets a link of its own, closed after its send; a link a send failed on is replaced for the next reply and closed once the replies still on it are done. Replies time out after `ResponseTimeout` (default 60 seconds).

A request with no reply within `ResponseTimeout` fails with a `TimeoutException` (`No reply to '{type}' from endpoint '{endpoint}' within {timeout}.`), the same on every broker transport. It is never an `OperationCanceledException`, which only the caller's own cancellation token produces, so a `catch (OperationCanceledException)` around a dispatch cannot swallow a remote that did not answer. A request still waiting when the transport stops fails with an `InvalidOperationException`, and so does one still waiting when the connection is lost (`The connection to the broker for endpoint '{endpoint}' was lost before the reply arrived.`): its reply queue went with the connection, so no reply can arrive, and the request fails at once rather than at its timeout.

## Connection recovery

AMQPNetLite does not reconnect, so the transport does. It watches the connection, its session, its two sender links, the reply receiver and every consumer link; when any of them closes without the transport closing it (a broker restart closes them all), it:

1. logs an Error (`ConnectionLost`, EventId 110, with the AMQP error the broker or the socket gave),
2. stops counting itself started, so a send fails at once with `InvalidOperationException` until it is back, and fails every request waiting for a reply (above),
3. reconnects, waiting 1, 2, 4, 8 and 16 seconds, then every 30 seconds, before each attempt, logging a Warning (`ReconnectFailed`, EventId 112) for each one that fails,
4. rebuilds everything on the new connection: the addresses and queues (idempotently, as at startup; in Developer and Test mode the queues are not durable, so a broker restart deleted them), the sender links, a new reply queue, and the consumers. It then logs `Reconnected` (Information, EventId 111).

Each connection attempt gives up after 30 seconds, at startup too. Stopping the host stops a reconnection in progress: it installs nothing, and a connection it was still building is closed.

A handler still running when the connection drops finishes, but its acknowledgement cannot reach the broker, which redelivers its message (unless a broker restart deleted the queue with it: Developer and Test queues are not durable), so it may run twice: make such handlers idempotent with the [inbox](../inbox.md). Its reply goes out on the new connection if there is one by then. An event published while the transport reconnects fails (the mediator logs `TransportBroadcastFailed` and carries on); one written through the [outbox](../outbox.md) is retried until the transport is back.

A connection that goes silent without closing (a half-open TCP connection after a network partition) is not detected until the operating system gives up on it: the transport sets no AMQP idle timeout.

## Failed messages

A failed message is never released back in a loop. Where one that must be kept goes depends on whether the host registered Conduit's [dead-letter queue](../dead-letter-queue.md):

- **With Conduit's dead-letter store** (`AddDurability(d => d.AddDeadLetterQueue())`), Conduit captures the failed message in its database, with the exception and the message context, and accepts it on the broker. The message is browsed and replayed through `IDeadLetters`; a replay is sent by fully qualified name (`address::queue`) to the queue this application takes the type from.
- **Without one**, the transport settles the delivery as `modified` with `delivery-failed`, which Artemis counts as a delivery attempt: it redelivers the message (after the address's `redelivery-delay`, 0 by default) until the address's `max-delivery-attempts` (10 by default), then moves it to the address's dead-letter address (`DLQ` on a stock broker), with `_AMQ_ORIG_ADDRESS` and `_AMQ_ORIG_QUEUE` naming where it came from. Both are broker settings (`address-settings` in `broker.xml`). Today this is a failed event listener (`EventListenerException`), and a routed `[FireAndForget]` command whose dispatch itself threw: what Conduit's store would otherwise have captured. A message Conduit settles without a store (an undispatchable body, a refused event, a request whose failure was already answered to its caller) is accepted, as on every broker.

A failure is never settled as `released`: Artemis does not count a released delivery, so a listener that always failed would get its event again at once, forever. A message delivered after the [drain](index.md#stopping-a-host-with-work-in-flight) began is not a failure: it was never dispatched, and it is `released` for another instance to take, without counting a delivery attempt.

## Stopping

When the host stops, the transport follows the [shared stop sequence](index.md#stopping-a-host-with-work-in-flight). It stops reconnecting, if it was, and stops replenishing its consumer links' credit, keeping the links open so a handler still running accepts its message on the link it arrived on. AMQPNetLite sends the broker nothing for that, so Artemis can still push as many messages as a link's outstanding credit allows (at most its prefetch); each is released undispatched. A real withdrawal would need AMQPNetLite's drain mode, which zeroes the link's local credit at once: a message already in flight under the old credit then fails the link's transfer-limit check, and that closes the whole connection. Once the handlers have drained, the consumer links are closed (the broker releases anything still unsettled), and the connection, with the reply queue, is closed when the host disposes the transport.

## Auto-discovery

The transport reads `ConduitTypeRegistry` — populated at compile time by the source generator — to learn which types this application handles:

- `IEventListener<T>` — adds the event type to the filter of the per-app queue on the multicast address (or of its pool's event queue)
- `ICommandHandler<T>`, `ICommandHandler<T,R>`, `IQueryHandler<T,R>` — creates or joins the shared queue on the anycast address for each command/query type

A listener registered at runtime counts too: `AddJobs` (an event trigger's event), `AddAuditListener`
(`AuditBatch`) and a `ConfigureConduitRegistrations` `HandleEvent` override activate their event in the
registry (`ConduitTypeRegistry.ActivateEvent`), so it is subscribed like an event whose listener the
generator found.

There is no runtime DI scanning and no manual binding configuration.

## Wire format

Messages use JSON serialization mapped onto AMQP 1.0 properties:

| Field | Value |
|---|---|
| Body | JSON-serialized message payload |
| `Subject` | Full type name (e.g. `"Orders.Commands.CreateOrder"`) |
| `MessageId` | From `MessageContext` or auto-generated |
| `CorrelationId` | From `MessageContext.ConversationId` |
| Property `CausationId` | From `MessageContext.CausationId` — historical name, kept for existing consumers |
| Property `ReplyTo`, `ReplyCorrelationId` | Reply routing (requests only) |
| Property `conduit-message-id`, `conduit-causation-id`, `conduit-conversation-id` | Canonical forms of the same three values, written alongside the native and historical names above |
| Property `conduit-source-app` | The publishing application's `ApplicationName`, used by the [self-published event filter](index.md#self-published-events) (omitted when `ApplicationName` is unset) |

## Receiver pools

For concurrency isolation, configure named receiver pools:

```csharp
endpoint.UseActiveMQ(connectionString, options =>
{
    options.DefaultConcurrency = 20;

    options.AddPool("imports", pool =>
    {
        pool.Concurrency = 2;
        pool.Handle("Catalog.Commands.BulkImportCommand");
    });
});
```

This routes `BulkImportCommand` to a dedicated pool queue with link credit 2 (`pool.Concurrency` maps to the receiver link prefetch count for ActiveMQ). Everything else uses the default queues at credit 20. A pool can take event types too: the application's subscribed events among the pool's types go to one pool event queue, filtered to them, and the application's own event queue filters them out, so a pooled event is delivered once, on the pool's queue.

Link credit controls how many unacknowledged messages the broker will push to a receiver at once. Lower values limit throughput but stop a slow consumer from buffering work it cannot get to.

`AddPool` is get-or-create by name: a pool can be declared in configuration (`Pools` array under the
endpoint's section) and configured again in code on the same name, and the two merge into one pool
rather than producing a duplicate. Name matching is case-insensitive, and because configuration
binds first, the configured spelling of `Name` is the one that survives into queue naming.
`pool.Handle(...)` de-duplicates, so the merged pool's message types are the union of what
configuration listed and what code added:

```jsonc
{ "Name": "imports", "Concurrency": 2, "MessageTypes": [ "Catalog.Commands.BulkImportCommand" ] }
```
```csharp
options.AddPool("imports", pool => pool.Handle<ReindexCatalogCommand>());
// -> one pool "imports", Concurrency 2, handling
//    [Catalog.Commands.BulkImportCommand, Catalog.Commands.ReindexCatalogCommand]
```

---

Previous: [Amazon SQS](amazon-sqs.md) | [Home](../../README.md) | Next: [Pipeline Behaviors](../pipeline-behaviors.md)
