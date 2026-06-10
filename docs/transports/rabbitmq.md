# RabbitMQ

- [Overview](#overview)
- [Setup](#setup)
- [Options](#options)
- [Topology](#topology)
  - [Unicast correctness](#unicast-correctness)
- [Developer and test modes](#developer-and-test-modes)
- [Request/response](#requestresponse)
- [Auto-discovery](#auto-discovery)
- [Wire format](#wire-format)
- [Receiver pools](#receiver-pools)

## Overview

The RabbitMQ transport provides durable, broker-based messaging over AMQP. All message types (commands, queries, events) flow through a single topic exchange with routing keys. Commands and queries use request/response with correlation IDs and a transient reply queue. The transport runs as an `IHostedService` that manages topology, consumers, and reply correlation.

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
| `DefaultConcurrency` | `10` | Default consumer concurrency. For RabbitMQ this maps to the consumer channel prefetch count (`BasicQos`). |

Exchange and queue names are controlled by `SystemName` and `TopologyMode` on `ConduitOptions` (see [Developer and test modes](#developer-and-test-modes)).

## Topology

The transport declares the following on startup (idempotent — safe to run on every startup):

```
Exchange: {systemName} (type: topic)
Queue: {systemName}-{appName}
├── Binding: routing key = {FullTypeName} (per handled type)
Queue: {systemName}-{appName}-{poolName}
├── Binding: routing key = {FullTypeName} (per pool type)
Reply Queue: exclusive auto-delete (per connection)
```

All message types (commands, queries, events) flow through a single topic exchange. There is no direct exchange. Each handled type gets a queue binding with the full type name as the routing key. Reply messages bypass the exchange and go directly to the exclusive reply queue.

All declarations are idempotent.

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

Replies time out after 30 seconds. Reply messages bypass the topic exchange.

## Auto-discovery

The transport scans DI at startup for all handler registrations:

- `IEventListener<T>` — binds the app queue to the topic exchange for each event type
- `ICommandHandler<T>`, `ICommandHandler<T,R>`, `IQueryHandler<T,R>` — binds the app queue for each command/query type

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
| Header `CausationId` | From `MessageContext.CausationId` |
| Header `ConversationId` | From `MessageContext.ConversationId` |

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

This routes `BulkImportCommand` to a dedicated `{systemName}-{appName}-imports` queue with prefetch 2 (`pool.Concurrency` maps to the consumer prefetch count for RabbitMQ). Everything else uses the default queue at prefetch 20.

Prefetch count controls how many unacknowledged messages RabbitMQ delivers to a consumer at once. Lower values limit throughput but prevent a slow consumer from buffering too many messages.

---

Previous: [Azure Service Bus](azure-service-bus.md) | [Home](../../README.md) | Next: [Pipeline Behaviors](../pipeline-behaviors.md)
