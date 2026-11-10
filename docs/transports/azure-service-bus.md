# Azure Service Bus

- [Overview](#overview)
- [Setup](#setup)
- [Options](#options)
  - [Loading from configuration](#loading-from-configuration)
- [Topology](#topology)
  - [Unicast vs multicast subscriptions](#unicast-vs-multicast-subscriptions)
- [Developer and test modes](#developer-and-test-modes)
- [Request/response](#requestresponse)
- [Auto-discovery](#auto-discovery)
- [Wire format](#wire-format)
- [Receiver pools](#receiver-pools)

## Overview

The Azure Service Bus transport provides durable, broker-based messaging. All message types (commands, queries, events) flow through a shared topic. Subscriptions with SQL filters route messages to per-app queues. Commands and queries use request/response over session-enabled reply queues. The transport runs as an `IHostedService` that manages topology, receivers, and reply correlation.

## Setup

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("orders", endpoint =>
    {
        endpoint.UseAzureServiceBus("Endpoint=sb://my-namespace.servicebus.windows.net/...");
        endpoint.RouteNamespace<Orders.Commands.CreateOrder>();
    });
});
```

`UseAzureServiceBus` registers the transport as both `IConduitTransport` (for outbound dispatch) and `IHostedService` (for inbound message processing). The transport starts when the host starts and stops gracefully on shutdown.

## Options

Pass a configuration callback for additional settings:

```csharp
endpoint.UseAzureServiceBus(connectionString, options =>
{
    options.DefaultConcurrency = 10;       // default: 10
});
```

| Property | Default | Description |
|---|---|---|
| `ConnectionString` | *(required)* | The Azure Service Bus connection string |
| `DefaultConcurrency` | `10` | Max concurrent message processing on the default receiver |
| `AdminConnectionString` | `null` | Separate connection string for topology management. Required when using the ASB Emulator with different AMQP/HTTP ports |
| `ResponseTimeout` | `60s` | Timeout for request/response operations |

Topic and queue names are controlled by `SystemName` and `TopologyMode` on `ConduitOptions` (see [Developer and test modes](#developer-and-test-modes)).

### Loading from configuration

Every property above also binds from `Conduit:Endpoints:{endpointName}`, so `UseAzureServiceBus()` can run with no arguments:

```json
{
  "Conduit": {
    "Endpoints": {
      "orders": {
        "ConnectionString": "Endpoint=sb://my-namespace.servicebus.windows.net/...",
        "DefaultConcurrency": 20
      }
    }
  }
}
```

```csharp
conduit.AddEndpoint("orders", endpoint => endpoint.UseAzureServiceBus());
```

Configuration binds first; an explicit `UseAzureServiceBus(connectionString)` argument or a `configure` callback overrides it. See [Loading from IConfiguration](../configuration-reference.md#loading-from-iconfiguration) for the full precedence rule, the pool-merge behavior, and the complete key reference.

## Topology

The transport creates the following on startup (idempotent — safe to run on every startup):

```
Topic: {systemName}
├── Subscription: {ReversedTypeName}
│   (unicast — command/query, SQL filter: Subject = '{FullTypeName}',
│    forwards to handler's app queue)
├── Subscription: {appName}-{ReversedTypeName}
│   (multicast — event, SQL filter: Subject = '{FullTypeName}',
│    forwards to subscribing app's queue)
Queue: {systemName}-{appName}
Queue: {systemName}-{appName}-replies (session-enabled, messages expire after 2 × ResponseTimeout, at least 1 s)
Queue: {systemName}-{appName}-{poolName} (optional)
```

All messages (commands, queries, events) publish to the shared topic. Subscriptions with SQL filters (`Subject = '{FullTypeName}'`) forward matching messages to the appropriate app queue. Replies are the only exception — they go point-to-point to the session-enabled reply queue.

All creation is idempotent. Multiple pods starting simultaneously will not conflict (409 responses are swallowed).

A subscription that already exists but forwards to a different queue than this application's configuration names is either this application's to redirect or a collision:

- **This application's own.** An event subscription (its name carries the application's name), or a command or query subscription that forwards to another of this application's queues — its app queue or one of its configured pools. That is what moving a type into a pool, or back out of one, leaves behind. Startup updates the subscription's forwarding to the queue the configuration names now and logs `SubscriptionRedirected`.
- **Another application's.** A command or query subscription that forwards to any other queue: a second application handles the same command or query type, which unicast routing forbids. Startup fails with an `InvalidOperationException` (`... This indicates a naming collision between applications. If that queue belonged to this application (a pool it no longer has), delete the subscription.`) rather than take the subscription over.

A command or query subscription that forwards to a pool queue this release does not have is the second case, since that queue is not one of its own. So a release that removes a pool still holding a command or query fails to start until that subscription is deleted, and so does the previous release (a rollback, or an old instance restarting mid-rollout) once a newer one has moved a command or query into a pool the previous release lacks. Delete the subscription, and the next startup creates it afresh. While two releases that both have the queues involved run side by side, the instance that started last decides where the subscription forwards.

An event subscription is always redirected, never refused, because its name carries the application's name. Names are hyphen-joined, though, so two applications can compute the same one: application `a-b` listening for event `X` and application `a` listening for event `X.b` both get `{systemName}-a-b-X`. Rather than fail, each start redirects that shared subscription to its own application's queue, and the other application stops receiving the event until it restarts. Choose application names that cannot produce such a pair.


### Unicast vs multicast subscriptions

Commands and queries use **unicast subscriptions** with a shared name (no app prefix). Because the subscription name is the same across all apps, only one app receives each command or query message. This ensures exactly-one-handler semantics.

Events use **multicast subscriptions** with an app-specific prefix. Each subscribing app gets its own subscription, so every subscriber receives a copy of the event — including the app that published it, since its subscription matches the `Subject` filter like any other. See [Self-published events](index.md#self-published-events) for how the inbound side avoids running that application's own listeners twice.

Resource names are generated by `TopologyNaming` in core. Type name suffixes are reversed and hyphenated (e.g. `Orders.Commands.CreateOrder` becomes `CreateOrder-Commands-Orders`) and truncated with a hash if they exceed the ASB 50-character subscription name limit.

Every resource name is sanitized to `[A-Za-z0-9_-]`, any other character becoming `-`. The default `ApplicationName` is the entry assembly's name, so an application named `Conduit.Sample.Orders` gets the queue `conduit-Conduit-Sample-Orders`, not `conduit-Conduit.Sample.Orders`.

## Developer and test modes

`TopologyMode` on `ConduitOptions` controls resource naming and durability:

| Mode | Prefix | Resources | Auto-delete |
|---|---|---|---|
| `Production` | None | Durable | Never |
| `Developer` | `{TopologyPrefix}` (default: username) | Non-durable | 4 hours |
| `Test` | `{TopologyPrefix}` (default: `test-{random}`) | Non-durable | 5 minutes |

Developer mode gives each developer isolated broker resources. Test mode creates fully isolated, short-lived resources for integration tests.

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.TopologyMode = TopologyMode.Developer;
    conduit.TopologyPrefix = "myprefix";  // optional, defaults to Environment.UserName

    conduit.AddEndpoint("orders", endpoint =>
        endpoint.UseAzureServiceBus(connectionString));
});
```

Or via `appsettings.Development.json`:

```json
{
  "Conduit": {
    "TopologyMode": "Developer"
  }
}
```

## Request/response

When the generated mediator routes a command to an ASB endpoint:

1. The transport publishes the command to the shared topic with the `Subject` set to the full type name.
2. The unicast subscription forwards the message to the handler's app queue.
3. The receiving service processes the command and sends a reply to the reply queue with the matching session ID.
4. The sender accepts the session, receives the reply, and deserializes the `DispatchResult`.

Replies time out after `ResponseTimeout` (default 60 seconds). Reply messages bypass the topic and go directly to the session-enabled reply queue.

A request with no reply within `ResponseTimeout` fails with a `TimeoutException` (`No reply to '{type}' from endpoint '{endpoint}' within {timeout}.`), the same on every broker transport. It is never an `OperationCanceledException`, which only the caller's own cancellation token produces, so a `catch (OperationCanceledException)` around a dispatch cannot swallow a remote that did not answer. A request still waiting when the transport stops fails at once with an `InvalidOperationException` (`The transport for endpoint '{endpoint}' stopped before the reply arrived.`), rather than waiting out its `ResponseTimeout`.

A reply nobody will read does not stay in the reply queue. The sender reads its reply as soon as it arrives or never (the request timed out, or the sender stopped or crashed), and nothing else accepts a request's session, so each reply carries a time to live of twice the replying endpoint's `ResponseTimeout`, and the reply queue is created with a default message time to live of twice the requesting endpoint's. Service Bus discards a reply when either passes, where it would otherwise stay until the queue reached its size quota and every reply failed. A reply queue that already exists keeps the settings it was created with; the reply's own time to live still applies.

The time to live is never shorter than one second, the least Service Bus accepts, so a `ResponseTimeout` under 500 ms still gives one second. A `ResponseTimeout` with no finite double (`Timeout.InfiniteTimeSpan`, zero or less, or more than half of `TimeSpan.MaxValue`) sets no time to live at all, on the reply or on the queue: such replies never expire.

A handler still running when its host stops replies and completes its message before the transport closes, as on every broker (see [Stopping a host with work in flight](index.md#stopping-a-host-with-work-in-flight)): the transport replies and settles without the processor's cancellation token, which stopping cancels.

## Auto-discovery

The transport reads `ConduitTypeRegistry` — populated at compile time by the source generator — to learn which types this application handles:

- `IEventListener<T>` — creates multicast (per-app) subscriptions
- `ICommandHandler<T>`, `ICommandHandler<T,R>`, `IQueryHandler<T,R>` — creates unicast (shared) subscriptions

A listener registered at runtime counts too: `AddJobs` (an event trigger's event), `AddAuditListener`
(`AuditBatch`) and a `ConfigureConduitRegistrations` `HandleEvent` override activate their event in the
registry (`ConduitTypeRegistry.ActivateEvent`), so it is subscribed like an event whose listener the
generator found.

No manual subscription configuration is needed. When an event is published, the transport sends it to the shared topic with the event's full type name as the `Subject` property. Subscriptions filter by subject and forward matching messages to the appropriate queue.

## Wire format

Messages use JSON serialization with type information in message properties:

| Property | Value |
|---|---|
| Body | JSON-serialized message payload |
| `Subject` | Full type name (e.g. `"Orders.Commands.CreateOrder"`) |
| `ContentType` | `"application/json"` |
| `MessageId` | From `MessageContext` or auto-generated |
| `CorrelationId` | From `MessageContext.ConversationId` |
| Application property `CausationId` | From `MessageContext.CausationId` — historical name, kept for existing consumers |
| Application properties `conduit-message-id`, `conduit-causation-id`, `conduit-conversation-id` | Canonical forms of the same three values, written alongside `MessageId`/`CorrelationId`/the historical `CausationId` property above |
| Application property `conduit-source-app` | The publishing application's `ApplicationName`, used by the [self-published event filter](index.md#self-published-events) (omitted when `ApplicationName` is unset) |

## Receiver pools

For workloads that need concurrency isolation, configure named receiver pools. Each pool gets its own queue and processor with independent concurrency settings.

```csharp
endpoint.UseAzureServiceBus(connectionString, options =>
{
    options.DefaultConcurrency = 20;

    options.AddPool("imports", pool =>
    {
        pool.Concurrency = 2;
        pool.Handle("Catalog.Commands.BulkImportCommand");
    });
});
```

This routes `BulkImportCommand` to a dedicated `{systemName}-{appName}-imports` queue processed with concurrency 2, while everything else uses the default queue at concurrency 20. Use pools to prevent slow or resource-intensive commands from blocking normal traffic.

Moving a type into a pool, out of one, or between two takes effect at the next startup, which redirects the type's existing subscription to its new queue (see [Topology](#topology)). The exception is a command or query whose subscription forwards to a pool queue the starting release does not have (the pool was removed, or a rollback to a release without it): that release fails to start until the subscription is deleted. An event's subscription is redirected regardless.

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

Previous: [HTTP](http.md) | [Home](../../README.md) | Next: [RabbitMQ](rabbitmq.md)
