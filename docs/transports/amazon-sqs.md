# Amazon SQS

- [Overview](#overview)
- [Setup](#setup)
- [Options](#options)
  - [Loading from configuration](#loading-from-configuration)
  - [Credentials](#credentials)
  - [LocalStack](#localstack)
- [Topology](#topology)
  - [Filter policies and raw message delivery](#filter-policies-and-raw-message-delivery)
  - [Name length](#name-length)
- [Developer and test modes](#developer-and-test-modes)
- [Request/response](#requestresponse)
- [Auto-discovery](#auto-discovery)
- [Wire format](#wire-format)
- [Receiver pools](#receiver-pools)

## Overview

The Amazon SQS transport provides durable, broker-based messaging over SNS and SQS together: **SNS** carries the fan-out, **SQS** holds the per-application queues. All message types (commands, queries, events) are published to a single SNS topic, and SNS subscription filter policies route each message to the queues that should receive it. Commands and queries use request/response with a per-application reply queue. The transport runs as an `IHostedService` that manages topology, long-polling consumers, and reply correlation.

Unlike the other broker transports there is no connection string — the transport authenticates with an AWS region and the standard credential chain.

## Setup

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("events", endpoint =>
    {
        endpoint.UseAmazonSqs(options => options.Region = RegionEndpoint.EUWest1);
        endpoint.RouteNamespace<Orders.Commands.CreateOrder>();
    });
});
```

`UseAmazonSqs` registers the transport as both `IConduitTransport` and `IHostedService`. The transport starts when the host starts. The callback is optional when the settings come from configuration.

## Options

```csharp
endpoint.UseAmazonSqs(options =>
{
    options.Region = RegionEndpoint.EUWest1;
    options.VisibilityTimeoutSeconds = 120;   // default: 60
});
```

| Property | Default | Description |
|---|---|---|
| `Region` | *(none)* | AWS region. Falls back to the SDK's own region resolution when unset. |
| `ServiceUri` | `null` | Override endpoint for both SNS and SQS — used for [LocalStack](#localstack) |
| `Credentials` | `null` | Explicit AWS credentials. When unset, the default AWS credential chain applies. |
| `DefaultConcurrency` | `10` | Default consumer concurrency. For SQS this bounds polling parallelism. |
| `ResponseTimeout` | `60s` | Timeout for request/response operations |
| `VisibilityTimeoutSeconds` | `60` | SQS visibility timeout. Must be **≥** `ResponseTimeout`, validated at startup. |
| `RawMessageDelivery` | `true` | Required to stay `true` — see [Filter policies and raw message delivery](#filter-policies-and-raw-message-delivery) |

Topic and queue names are controlled by `SystemName` and `TopologyMode` on `ConduitOptions` (see [Developer and test modes](#developer-and-test-modes)).

### Loading from configuration

Every property above also binds from `Conduit:Endpoints:{endpointName}`, so `UseAmazonSqs()` can run with no callback at all:

```json
{
  "Conduit": {
    "Endpoints": {
      "events": {
        "Region": "eu-west-1",
        "VisibilityTimeoutSeconds": 120,
        "DefaultConcurrency": 20
      }
    }
  }
}
```

```csharp
conduit.AddEndpoint("events", endpoint => endpoint.UseAmazonSqs());
```

`Region` is written as an AWS **system name** (`"eu-west-1"`), not a .NET constant name. Configuration binds first; a `configure` callback overrides it. See [Loading from IConfiguration](../configuration-reference.md#loading-from-iconfiguration) for the full precedence rule, the pool-merge behavior, and the complete key reference.

### Credentials

Credentials are nested under `Credentials`, mirroring the property they populate:

```json
{
  "Conduit": {
    "Endpoints": {
      "events": {
        "Region": "eu-west-1",
        "Credentials": {
          "AccessKey": "AKIA…",
          "SecretKey": "…",
          "SessionToken": "…"
        }
      }
    }
  }
}
```

`SessionToken` is optional — supply it for temporary credentials, omit it for long-lived ones. If either `AccessKey` or `SecretKey` is missing, the transport leaves `Credentials` unset and the **default AWS credential chain** applies (environment variables, profile, container or instance role). That is usually what you want in a deployed environment; prefer it to putting secrets in appsettings.

### LocalStack

Point both SNS and SQS at a local endpoint with `ServiceUri`:

```json
{
  "Conduit": {
    "Endpoints": {
      "events": {
        "Region": "us-east-1",
        "ServiceUri": "http://localhost:4566"
      }
    }
  }
}
```

When `ServiceUri` is set, `Credentials` is optional: LocalStack does not validate credentials, but the AWS SDK insists on *a* credential source, so the transport substitutes a placeholder pair when none is configured. Supply real ones only if your local stack checks them.

## Topology

The transport creates the following on startup (idempotent — safe to run on every startup):

```
SNS topic: {systemName}
SQS queue: {systemName}-{appName}
├── SNS subscription, filter policy on message type
SQS queue: {systemName}-{appName}-{poolName}
├── SNS subscription, filter policy on the pool's types
SQS queue: {systemName}-{appName}-replies
```

Every message — command, query or event — is published to the single SNS topic. Subscription filter policies decide which queues receive a copy:

- **Commands and queries** use a shared subscription name, so exactly one application receives each message (unicast).
- **Events** use a per-application subscription name, so every subscribing application receives its own copy (multicast).

Replies bypass SNS entirely and are sent directly to the sender's reply queue.

### Filter policies and raw message delivery

SNS filter policies match on **message attributes**, and attributes are only visible to a filter policy when the subscription uses raw message delivery. Without it, SNS wraps the payload in a JSON envelope and the attributes move into the body, where filter policies cannot see them — so every queue would receive every message.

For that reason `RawMessageDelivery` defaults to `true` and the transport validates it at startup, throwing rather than starting up with silently-broken routing.

### Name length

SQS queue names are capped at **80 characters**. `TopologyNaming` truncates and hashes names that would exceed the cap, so a long `SystemName`, application name or pool name will produce a hashed suffix rather than a failure. Names stay stable and collision-resistant, but they will not always be human-readable — keep `SystemName` and application names short if you want to recognise your queues in the AWS console.

## Developer and test modes

`TopologyMode` on `ConduitOptions` controls resource naming:

| Mode | Prefix | Resources |
|---|---|---|
| `Production` | None | Standard |
| `Developer` | `{TopologyPrefix}` (default: username) | Prefixed |
| `Test` | `{TopologyPrefix}` (default: `test-{random}`) | Prefixed |

Developer mode gives each developer isolated topics and queues (e.g. `bpage-conduit` instead of `conduit`). Test mode creates isolated, short-lived resources for integration tests.

SNS and SQS have no auto-delete, so ephemeral resources accumulate. `AmazonSqsTopologyReaper.ReapByPrefixAsync(options, prefix)` deletes queues and topics whose names start with a prefix — deleting a topic removes its subscriptions with it. The test harness calls it once per project, and it is available to applications for the same purpose.

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.TopologyMode = TopologyMode.Developer;
    conduit.TopologyPrefix = "myprefix";  // optional, defaults to Environment.UserName

    conduit.AddEndpoint("events", endpoint =>
        endpoint.UseAmazonSqs(options => options.Region = RegionEndpoint.EUWest1));
});
```

## Request/response

When the generated mediator routes a command to an Amazon SQS endpoint:

1. The transport publishes the command to the SNS topic with `ReplyQueueAddress` and `CorrelationId` attributes.
2. The subscription filter policy routes it to the handling application's queue.
3. The handler runs and the reply is sent **directly to the sender's reply queue** with the matching correlation id, bypassing SNS.
4. The sender matches the reply by correlation id and deserializes the `DispatchResult`.

Replies time out after `ResponseTimeout` (default 60 seconds). `VisibilityTimeoutSeconds` must be at least `ResponseTimeout`, or a message could become visible again — and be redelivered to another consumer — while the original caller is still waiting for its reply. The transport validates this at startup.

A request with no reply within `ResponseTimeout` fails with a `TimeoutException` (`No reply to '{type}' from endpoint '{endpoint}' within {timeout}.`), the same on every broker transport. It is never an `OperationCanceledException`, which only the caller's own cancellation token produces, so a `catch (OperationCanceledException)` around a dispatch cannot swallow a remote that did not answer. A request still waiting when the transport stops fails with an `InvalidOperationException`.

## Auto-discovery

The transport reads `ConduitTypeRegistry` — populated at compile time by the source generator — to learn which types this application handles:

- `IEventListener<T>` — adds the event type to the per-app subscription's filter policy
- `ICommandHandler<T>`, `ICommandHandler<T,R>`, `IQueryHandler<T,R>` — adds the type to the shared subscription's filter policy

A listener registered at runtime counts too: `AddJobs` (an event trigger's event), `AddAuditListener`
(`AuditBatch`) and a `ConfigureConduitRegistrations` `HandleEvent` override activate their event in the
registry (`ConduitTypeRegistry.ActivateEvent`), so it is subscribed like an event whose listener the
generator found.

There is no runtime DI scanning and no manual subscription configuration.

## Wire format

Messages use JSON serialization with metadata in SNS/SQS **message attributes**:

| Attribute | Value |
|---|---|
| Body | JSON-serialized message payload |
| `Type` | Full type name (e.g. `"Orders.Commands.CreateOrder"`) |
| `MessageId` | From `MessageContext` or auto-generated |
| `CausationId` | From `MessageContext.CausationId` |
| `ConversationId` | From `MessageContext.ConversationId` |
| `ReplyQueueAddress` | Reply queue URL (requests only) |
| `CorrelationId` | Request/response correlation (requests only) |
| `traceparent`, `tracestate` | W3C trace context |
| `conduit-source-app` | The publishing application's `ApplicationName`, used by the [self-published event filter](index.md#self-published-events) (omitted when `ApplicationName` is unset) |

The three correlation identifiers use short names rather than the canonical `conduit-message-id` style the other transports carry. That is a hard constraint, not a style choice: **SNS and SQS cap a message at 10 attributes**, and a request publish already uses 9 of them. Renaming the three correlation ids keeps the message under the ceiling where carrying both forms would not, leaving exactly one attribute spare.

Keep that ceiling in mind if you were hoping to add your own attributes to Conduit messages — there is almost no room.

## Receiver pools

For concurrency isolation, configure named receiver pools:

```csharp
endpoint.UseAmazonSqs(options =>
{
    options.Region = RegionEndpoint.EUWest1;
    options.DefaultConcurrency = 20;

    options.AddPool("imports", pool =>
    {
        pool.Concurrency = 2;
        pool.Handle("Catalog.Commands.BulkImportCommand");
    });
});
```

This routes `BulkImportCommand` to a dedicated `{systemName}-{appName}-imports` queue with its own SNS subscription and filter policy, polled with a parallelism bound of 2. Everything else uses the default queue at 20. A slow pool handler cannot starve the default queue's consumers.

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

Remember the [80-character queue-name cap](#name-length) when naming pools — a long pool name on a long application name will be hashed.

---

Previous: [RabbitMQ](rabbitmq.md) | [Home](../../README.md) | Next: [ActiveMQ](activemq.md)
