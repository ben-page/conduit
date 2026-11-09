# ActiveMQ

- [Overview](#overview)
- [Setup](#setup)
- [Options](#options)
  - [Loading from configuration](#loading-from-configuration)
- [Topology](#topology)
  - [Anycast and multicast](#anycast-and-multicast)
- [Developer and test modes](#developer-and-test-modes)
- [Request/response](#requestresponse)
- [Auto-discovery](#auto-discovery)
- [Wire format](#wire-format)
- [Receiver pools](#receiver-pools)

## Overview

The ActiveMQ transport provides durable, broker-based messaging over **AMQP 1.0** against **ActiveMQ Artemis**, using `AMQPNetLite.Core`. Artemis separates the *address* a message is sent to from the *queues* bound to it, and Conduit uses that split directly: one anycast address carries commands and queries, one multicast address carries events. Commands and queries use request/response over a per-instance temporary reply queue. The transport runs as an `IHostedService` that manages topology, receiver links, and reply correlation.

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
Address: {systemName}-commands                 (ANYCAST)
├── Queue: {systemName}-{FullTypeName}         filter: JMSType='{FullTypeName}'
├── Queue: {systemName}-{poolName}             filter: pool's types

Address: {systemName}-events                   (MULTICAST)
├── Queue: {systemName}-{appName}              per-app copy of every subscribed event
├── Queue: {systemName}-{appName}-{poolName}   pool's event types

Reply queue: per-instance, temporary (destroyed on shutdown)
```

### Anycast and multicast

Artemis routes by address type, which maps exactly onto Conduit's two delivery semantics:

- **Commands and queries** go to the **anycast** address. Each command or query type gets one shared queue, named from the type, so exactly one consumer receives each message (unicast). Queues carry a `JMSType` filter so a message reaches only the queue for its own type.
- **Events** go to the **multicast** address. Each application gets its own queue bound to that address, so every subscribing application receives a copy.

Because the broker enforces the distinction, unicast correctness does not depend on convention here — unlike RabbitMQ, where two applications binding the same command type silently round-robin between them.

## Developer and test modes

`TopologyMode` on `ConduitOptions` controls resource naming and lifetime:

| Mode | Prefix | Durability | Auto-delete |
|---|---|---|---|
| `Production` | None | Durable | Never |
| `Developer` | `{TopologyPrefix}` (default: username) | Non-durable | After 4 hours idle |
| `Test` | `{TopologyPrefix}` (default: `test-{random}`) | Non-durable | After 5 minutes idle |

Developer mode gives each developer isolated broker resources (e.g. address `bpage-conduit-commands` instead of `conduit-commands`). Test mode creates fully isolated, short-lived resources for integration tests.

Artemis supports the delay natively through `auto-delete-delay`, so ephemeral resources clean themselves up — no reaper is needed here, unlike Amazon SQS.

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

Each transport instance creates its own **exclusive temporary reply queue**, destroyed when the instance shuts down — so a reply reaches the instance that sent the request, and no reply queues are left behind. Replies time out after `ResponseTimeout` (default 60 seconds).

## Auto-discovery

The transport reads `ConduitTypeRegistry` — populated at compile time by the source generator — to learn which types this application handles:

- `IEventListener<T>` — binds the per-app queue on the multicast address for each event type
- `ICommandHandler<T>`, `ICommandHandler<T,R>`, `IQueryHandler<T,R>` — creates or joins the shared queue on the anycast address for each command/query type

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

This routes `BulkImportCommand` to a dedicated pool queue with link credit 2 (`pool.Concurrency` maps to the receiver link prefetch count for ActiveMQ). Everything else uses the default queues at credit 20.

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
