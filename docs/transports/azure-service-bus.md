# Azure Service Bus

- [Overview](#overview)
- [Setup](#setup)
- [Options](#options)
- [Topology](#topology)
- [Request/response](#requestresponse)
- [Event auto-subscription](#event-auto-subscription)
- [Wire format](#wire-format)
- [Receiver pools](#receiver-pools)

## Overview

The Azure Service Bus transport provides durable, broker-based messaging. Commands and queries use request/response over session-enabled reply queues. Events publish to a shared topic with auto-created subscriptions. The transport runs as an `IHostedService` that manages topology, receivers, and reply correlation.

## Setup

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("orders", endpoint =>
        endpoint.UseAzureServiceBus("Endpoint=sb://my-namespace.servicebus.windows.net/..."));

    conduit.Route("Orders.Commands").To("orders");
});
```

`UseAzureServiceBus` registers the transport as both `IConduitTransport` (for outbound dispatch) and `IHostedService` (for inbound message processing). The transport starts when the host starts and stops gracefully on shutdown.

## Options

Pass a configuration callback for additional settings:

```csharp
endpoint.UseAzureServiceBus(connectionString, options =>
{
    options.AutoCreateTopology = true;     // default: true
    options.DefaultConcurrency = 10;       // default: 10
    options.TopicName = "conduit";         // default: "conduit"
    options.QueuePrefix = "myapp";         // optional, overrides default "conduit" prefix
});
```

| Property | Default | Description |
|---|---|---|
| `AutoCreateTopology` | `true` | Create queues, topics, and subscriptions on startup |
| `DefaultConcurrency` | `10` | Max concurrent message processing on the default receiver |
| `TopicName` | `"conduit"` | Shared topic name for event pub/sub |
| `QueuePrefix` | `null` | Queue name prefix. When set, queues are `{prefix}-{appName}` instead of `conduit-{appName}` |
| `AdminConnectionString` | `null` | Separate connection string for topology management. Required when using the ASB Emulator with different AMQP/HTTP ports |

## Topology

When `AutoCreateTopology` is true, the transport creates the following on startup:

- **App queue**: `conduit-{appName}` for receiving commands and queries.
- **Reply queue**: `conduit-{appName}-replies` with sessions enabled for request/response correlation.
- **Shared topic**: `conduit` (or the configured `TopicName`) for event pub/sub.
- **Event subscriptions**: One per event type the service listens for, with a SQL filter on the `Subject` property forwarding to the app queue.

All creation is idempotent. Multiple pods starting simultaneously will not conflict (409 responses are swallowed).

Set `AutoCreateTopology = false` if your topology is managed externally through infrastructure-as-code.

## Request/response

When the generated mediator routes a command to an ASB endpoint:

1. The transport serializes the command and sends it to the target queue with `ReplyTo` and `ReplyToSessionId` set.
2. The receiving service processes the command and sends a reply to the reply queue with the matching session ID.
3. The sender accepts the session, receives the reply, and deserializes the `DispatchResult`.

Replies time out after 30 seconds.

## Event auto-subscription

The transport scans DI for `IEventListener<T>` registrations at startup and creates topic subscriptions with SQL filters for each event type. No manual subscription configuration is needed.

When an event is published, the transport sends it to the shared topic with the event's full type name as the `Subject` property. Subscriptions filter by subject and forward matching messages to the appropriate queue.

## Wire format

Messages use JSON serialization with type information in message properties:

| Property | Value |
|---|---|
| Body | JSON-serialized message payload |
| `Subject` | Full type name (e.g. `"Orders.Commands.CreateOrder"`) |
| `ContentType` | `"application/json"` |
| `MessageId` | From `MessageContext` or auto-generated |
| `CorrelationId` | From `MessageContext.ConversationId` |
| Application property `CausationId` | From `MessageContext.CausationId` |

## Receiver pools

For workloads that need concurrency isolation, configure named receiver pools. Each pool gets its own queue and processor with independent concurrency settings.

```csharp
endpoint.UseAzureServiceBus(connectionString, options =>
{
    options.DefaultConcurrency = 20;

    options.AddPool("imports", pool =>
    {
        pool.Concurrency = 2;
        pool.MessageTypes.Add("Catalog.Commands.BulkImportCommand");
    });
});
```

This routes `BulkImportCommand` to a dedicated `conduit-{appName}-imports` queue processed with concurrency 2, while everything else uses the default queue at concurrency 20. Use pools to prevent slow or resource-intensive commands from blocking normal traffic.

---

Previous: [HTTP](http.md) | [Home](../../README.md) | Next: [RabbitMQ](rabbitmq.md)
