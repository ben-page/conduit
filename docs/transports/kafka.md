# Kafka

- [Overview](#overview)
- [Setup](#setup)
- [Options](#options)
  - [Loading from configuration](#loading-from-configuration)
  - [librdkafka settings](#librdkafka-settings)
- [Topology](#topology)
  - [Consumer groups](#consumer-groups)
- [Concurrency and partitions](#concurrency-and-partitions)
- [Developer and test modes](#developer-and-test-modes)
- [Request/response](#requestresponse)
- [Auto-discovery](#auto-discovery)
- [Wire format](#wire-format)
- [Receiver pools](#receiver-pools)
- [Failure handling](#failure-handling)

## Overview

The Kafka transport provides durable, log-based messaging over `Confluent.Kafka`. All message types (commands, queries, events) flow through a single shared topic; routing to the right application is done with **consumer groups** rather than separate queues, and each consumer filters by message type on the client side. Commands and queries use request/response over a per-application reply topic. The transport runs as an `IHostedService` that manages topics, consumer groups, and reply correlation.

Kafka differs from the queue-based transports in one way worth knowing up front: because a partition is assigned to exactly one consumer in a group, **your effective parallelism is bounded by the shared topic's partition count**. See [Concurrency and partitions](#concurrency-and-partitions).

## Setup

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("telemetry", endpoint =>
    {
        endpoint.UseKafka("localhost:9092");
        endpoint.RouteNamespace<Telemetry.Commands.RecordReading>();
    });
});
```

`UseKafka` registers the transport as both `IConduitTransport` and `IHostedService`. The transport starts when the host starts.

## Options

```csharp
endpoint.UseKafka(bootstrapServers, options =>
{
    options.DefaultConcurrency = 4;        // default: 10
});
```

| Property | Default | Description |
|---|---|---|
| `BootstrapServers` | *(required)* | Kafka bootstrap servers, e.g. `"localhost:9092"` |
| `DefaultConcurrency` | `10` | Number of consumer instances in each default consumer group. Clamped to the shared topic's partition count — see [Concurrency and partitions](#concurrency-and-partitions). |
| `ResponseTimeout` | `60s` | Timeout for request/response operations |
| `ProducerConfig` | `null` | Confluent `ProducerConfig` applied to the producer |
| `ConsumerConfig` | `null` | Confluent `ConsumerConfig` applied to every consumer |

Topic names are controlled by `SystemName` and `TopologyMode` on `ConduitOptions` (see [Developer and test modes](#developer-and-test-modes)).

### Loading from configuration

Every property above also binds from `Conduit:Endpoints:{endpointName}`, so `UseKafka()` can run with no arguments:

```json
{
  "Conduit": {
    "Endpoints": {
      "telemetry": {
        "BootstrapServers": "kafka:9092",
        "DefaultConcurrency": 4,
        "ResponseTimeout": "00:00:30"
      }
    }
  }
}
```

```csharp
conduit.AddEndpoint("telemetry", endpoint => endpoint.UseKafka());
```

Configuration binds first; an explicit `UseKafka(bootstrapServers)` argument or a `configure` callback overrides it. See [Loading from IConfiguration](../configuration-reference.md#loading-from-iconfiguration) for the full precedence rule, the pool-merge behavior, and the complete key reference.

### librdkafka settings

`ProducerConfig` and `ConsumerConfig` bind from nested sections, and Kafka accepts **both spellings** of every setting — the C# property name and the native librdkafka name:

```json
{
  "Conduit": {
    "Endpoints": {
      "telemetry": {
        "BootstrapServers": "kafka:9092",
        "ProducerConfig": { "LingerMs": 5, "Acks": "All" },
        "ConsumerConfig": { "session.timeout.ms": "45000" }
      }
    }
  }
}
```

Both forms end up in the same underlying librdkafka dictionary, so `"LingerMs": 5` and `"linger.ms": "5"` are equivalent. The dotted form needs a second pass because the configuration binder matches on C# property names and would otherwise drop a dotted key silently.

An endpoint with no `ProducerConfig`/`ConsumerConfig` section leaves both properties `null`, so a `configure` callback that branches on `options.ProducerConfig is null` behaves as you would expect.

## Topology

The transport creates the following on startup (idempotent — safe to run on every startup):

```
Topic: {systemName}                              (6 partitions)
Topic: {systemName}-{appName}-replies
Topic: {systemName}-{appName}-dead-letter

Consumer groups:
  {systemName}                                   commands/queries — shared across apps (unicast)
  {systemName}-{appName}                         events — one group per app (multicast)
  {systemName}-…-pool-{poolName}                 one group per receiver pool
```

Everything — commands, queries and events — is produced to the single shared topic. There is no topic per message type.

### Consumer groups

Kafka delivers each message to **one consumer in each group** subscribed to the topic. Conduit uses that directly:

- **Commands and queries** are consumed by a group whose id is shared across every application, so exactly one application processes each message (unicast).
- **Events** are consumed by a group whose id includes the application name, so every subscribing application gets its own copy (multicast).

Because all types share one topic, each group receives messages it does not own. A consumer skips those, but **still commits their offset**, so the group that does own the type is the only one that processes it, and the skipped messages do not block the log.

The default groups own the *complement* of all pool-claimed types — every type not assigned to a pool.

## Concurrency and partitions

`DefaultConcurrency` is the number of consumer **instances** started in each default group; `pool.Concurrency` does the same for a pool's group. Kafka balances the shared topic's partitions across the instances in a group and commits offsets per instance, giving real parallelism with per-partition ordering and no manual offset tracking.

The shared topic is created with **6 partitions**, and instance counts are clamped to that number. This is inherent to Kafka: a partition is assigned to exactly one consumer in a group, so instances beyond the partition count sit idle. Setting `DefaultConcurrency = 50` does not give you fifty-way parallelism — it gives you six.

Outbound messages are partitioned by **message id**, not by type name, so same-type messages spread across partitions and a group with more than one instance can process them in parallel. The type discriminator travels in the `conduit-type` header, which keeps partition routing independent of type. Conduit makes no cross-message ordering guarantee between distinct dispatches, so distributing the key this way is safe.

## Developer and test modes

`TopologyMode` on `ConduitOptions` controls resource naming and retention:

| Mode | Prefix | Retention |
|---|---|---|
| `Production` | None | Broker default |
| `Developer` | `{TopologyPrefix}` (default: username) | 4 hours |
| `Test` | `{TopologyPrefix}` (default: `test-{random}`) | 5 minutes |

Developer mode gives each developer isolated topics (e.g. `bpage-conduit` instead of `conduit`). Test mode creates short-lived topics for integration tests. Retention is applied with `retention.ms`.

Kafka has no auto-delete, so ephemeral topics and consumer groups accumulate. `KafkaTopologyReaper.ReapByPrefixAsync(bootstrapServers, prefix)` deletes topics and consumer groups whose names start with a prefix — the test harness calls it once per project, and it is available to applications for the same purpose.

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.TopologyMode = TopologyMode.Developer;
    conduit.TopologyPrefix = "myprefix";  // optional, defaults to Environment.UserName

    conduit.AddEndpoint("telemetry", endpoint =>
        endpoint.UseKafka(bootstrapServers));
});
```

## Request/response

When the generated mediator routes a command to a Kafka endpoint:

1. The transport produces the command to the shared topic with `conduit-reply-topic` and `conduit-correlation-id` headers.
2. The handling application's shared command/query group consumes it.
3. The handler runs and the reply is produced to the sender's reply topic with the matching correlation id.
4. The sender matches the reply by correlation id and deserializes the `DispatchResult`.

Each application has its own reply topic, consumed by a per-instance consumer group with manual partition assignment — so a reply reaches the instance that sent the request, not a sibling replica. Replies time out after `ResponseTimeout` (default 60 seconds).

## Auto-discovery

The transport reads `ConduitTypeRegistry` — populated at compile time by the source generator — to learn which types this application handles:

- `IEventListener<T>` — the per-app event group processes each event type
- `ICommandHandler<T>`, `ICommandHandler<T,R>`, `IQueryHandler<T,R>` — the shared command/query group processes each type

There is no runtime DI scanning and no manual subscription configuration.

## Wire format

Messages use JSON serialization with metadata in Kafka headers (UTF-8 byte arrays):

| Field | Value |
|---|---|
| Value | JSON-serialized message payload |
| Key | The message id (partition key — see [Concurrency and partitions](#concurrency-and-partitions)) |
| `conduit-type` | Full type name (e.g. `"Orders.Commands.CreateOrder"`) |
| `conduit-message-id` | From `MessageContext` or auto-generated |
| `conduit-causation-id` | From `MessageContext.CausationId` |
| `conduit-conversation-id` | From `MessageContext.ConversationId` |
| `conduit-reply-topic` | Reply topic name (requests only) |
| `conduit-correlation-id` | Request/response correlation (requests only) |
| `conduit-source-app` | The publishing application's `ApplicationName`, used by the [self-published event filter](index.md#self-published-events) (omitted when `ApplicationName` is unset) |

Unlike RabbitMQ, Azure Service Bus and ActiveMQ — which also carry historical property names for backward compatibility — Kafka's on-wire header names have always matched the canonical `conduit-*` keys, so there is only one form to read.

## Receiver pools

For concurrency isolation, configure named receiver pools:

```csharp
endpoint.UseKafka(bootstrapServers, options =>
{
    options.DefaultConcurrency = 4;

    options.AddPool("imports", pool =>
    {
        pool.Concurrency = 2;
        pool.Handle("Catalog.Commands.BulkImportCommand");
    });
});
```

On the queue-based transports a pool is a dedicated queue. On Kafka a pool is a **dedicated consumer group**: it subscribes to the same shared topic and processes only the types in `pool.MessageTypes`, skipping and committing everything else. The net effect is the same — each message is processed exactly once, by the single group that owns its type — and pools are genuinely isolated, because a slow or blocking pool handler occupies its own group's instances and cannot stall the default group or another pool.

`pool.Concurrency` sets that group's instance count, clamped to the partition count like any other group.

`AddPool` is get-or-create by name: a pool can be declared in configuration (`Pools` array under the
endpoint's section) and configured again in code on the same name, and the two merge into one pool
rather than producing a duplicate. Name matching is case-insensitive, and because configuration
binds first, the configured spelling of `Name` is the one that survives into consumer-group naming.
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

## Failure handling

When a handler throws, the transport commits the offset and publishes the message to the application's dead-letter topic immediately. There is no in-process retry: Kafka offsets are sequential, so holding a failed message back would block every message behind it in the partition.

Application-level retry belongs in an `IPipelineBehavior<,>` — see [Pipeline Behaviors](../pipeline-behaviors.md) — where it can retry the handler without stalling the log.

---

Previous: [RabbitMQ](rabbitmq.md) | [Home](../../README.md) | Next: [Amazon SQS](amazon-sqs.md)
