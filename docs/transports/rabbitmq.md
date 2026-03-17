# RabbitMQ

The RabbitMQ transport provides durable, broker-based messaging over AMQP. Commands and queries use request/response with correlation IDs and a transient reply queue. Events publish to a topic exchange with routing keys. The transport runs as an `IHostedService` that manages topology, consumers, and reply correlation.

## Setup

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("orders", endpoint =>
        endpoint.UseRabbitMQ("amqp://guest:guest@localhost:5672/"));

    conduit.Route("Orders.Commands").To("orders");
});
```

`UseRabbitMQ` registers the transport as both `IConduitTransport` and `IHostedService`. The transport starts when the host starts.

## Options

```csharp
endpoint.UseRabbitMQ(connectionString, options =>
{
    options.AutoCreateTopology = true;       // default: true
    options.DefaultPrefetchCount = 10;       // default: 10
    options.ExchangeName = "conduit";        // default: "conduit"
    options.QueuePrefix = "myapp";           // optional
});
```

| Property | Default | Description |
|---|---|---|
| `AutoCreateTopology` | `true` | Declare exchanges, queues, and bindings on startup |
| `DefaultPrefetchCount` | `10` | Prefetch count for the default consumer channel |
| `ExchangeName` | `"conduit"` | Topic exchange name for event pub/sub |
| `QueuePrefix` | `null` | Queue name prefix. When set, queues are `{prefix}-{appName}` instead of `conduit-{appName}` |

## Topology

When `AutoCreateTopology` is true, the transport declares:

- **Topic exchange**: `conduit` for event pub/sub with routing keys.
- **Direct exchange**: `conduit.direct` for command/query routing.
- **App queue**: `conduit-{appName}`, durable, bound to the direct exchange.
- **Event bindings**: The app queue is bound to the topic exchange with routing keys for each subscribed event type.
- **Reply queue**: An exclusive auto-delete queue created per connection for request/response correlation.

All declarations are idempotent.

## Request/response

When the generated mediator routes a command to a RabbitMQ endpoint:

1. The transport publishes the command to the direct exchange with the target queue as the routing key. The message includes `ReplyTo` and `CorrelationId` properties.
2. The receiving service processes the command and publishes a reply to the sender's reply queue with the matching correlation ID.
3. The sender matches the reply by correlation ID and deserializes the `DispatchResult`.

Replies time out after 30 seconds.

## Event auto-subscription

The transport scans DI for `IEventListener<T>` registrations at startup and binds the app queue to the topic exchange with each event's full type name as the routing key.

When an event is published, the transport sends it to the topic exchange with the event's full type name as the routing key. RabbitMQ routes the message to all queues with matching bindings.

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
    options.DefaultPrefetchCount = 20;

    options.AddPool("imports", pool =>
    {
        pool.PrefetchCount = 2;
        pool.Handle("Catalog.Commands.BulkImportCommand");
    });
});
```

This routes `BulkImportCommand` to a dedicated `conduit-{appName}-imports` queue with prefetch 2. Everything else uses the default queue at prefetch 20.

Prefetch count controls how many unacknowledged messages RabbitMQ delivers to a consumer at once. Lower values limit throughput but prevent a slow consumer from buffering too many messages.

---

Previous: [Azure Service Bus](azure-service-bus.md) | [Home](../../README.md) | Next: [Pipeline Behaviors](../pipeline-behaviors.md)
