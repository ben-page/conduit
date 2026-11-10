# Azure Service Bus

- [Overview](#overview)
- [Setup](#setup)
- [Options and configuration keys](#options-and-configuration-keys)
  - [Loading from configuration](#loading-from-configuration)
- [Topology](#topology)
  - [Unicast and multicast subscriptions](#unicast-and-multicast-subscriptions)
  - [Auto-discovery](#auto-discovery)
- [Developer and Test modes](#developer-and-test-modes)
- [Request/response](#requestresponse)
- [Failure handling](#failure-handling)
  - [Stopping](#stopping)
- [Wire format](#wire-format)
- [Receiver pools](#receiver-pools)
- [Limitations](#limitations)

## Overview

The Azure Service Bus transport provides durable, broker-based messaging. All message types (commands, queries, events) flow through a shared topic. Subscriptions with SQL filters forward each message to the queue of the application that takes it. Commands and queries use request/response over a session-enabled reply queue. The transport runs as an `IHostedService` that manages topology, receivers, and reply correlation.

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

`UseAzureServiceBus` registers the transport as both `IConduitTransport` (for outbound dispatch) and `IHostedService` (for inbound message processing). The transport starts when the host starts and stops gracefully on shutdown. The namespace must support topics (the Standard or Premium tier).

## Options and configuration keys

Pass a configuration callback for additional settings:

```csharp
endpoint.UseAzureServiceBus(connectionString, options =>
{
    options.DefaultConcurrency = 20;
    options.ResponseTimeout = TimeSpan.FromSeconds(30);
});
```

`AzureServiceBusOptions`:

| Property | Default | Description |
|---|---|---|
| `ConnectionString` | *(required)* | The Azure Service Bus connection string. `UseAzureServiceBus` throws `InvalidOperationException` when neither code nor configuration supplies one. |
| `AdminConnectionString` | `null` | Separate connection string for topology management; `ConnectionString` when unset. Needed for the Service Bus emulator, which serves AMQP and its management API on different ports. |
| `DefaultConcurrency` | `10` | How many handlers the application queue's processor runs at once (its `MaxConcurrentCalls`). |
| `ResponseTimeout` | `00:01:00` | How long a request waits for its reply (see [Request/response](#requestresponse)). Positive and finite. |
| `Pools` | empty | Named receiver pools, added with `AddPool` (see [Receiver pools](#receiver-pools)). |
| `EndpointName` | the endpoint's name | Read-only: `UseAzureServiceBus` sets it from `AddEndpoint`, never from configuration. |

Topic and queue names are controlled by `SystemName`, `ApplicationName` and `TopologyMode` on `ConduitOptions` (see [Developer and Test modes](#developer-and-test-modes)).

### Loading from configuration

Every property above but `EndpointName` also binds from `Conduit:Endpoints:{endpointName}`, under the same name, so `UseAzureServiceBus()` can run with no arguments:

```json
{
  "Conduit": {
    "Endpoints": {
      "orders": {
        "ConnectionString": "Endpoint=sb://my-namespace.servicebus.windows.net/...",
        "DefaultConcurrency": 20,
        "ResponseTimeout": "00:00:30"
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

The transport creates the following on startup (idempotent: safe to run on every startup):

```
Topic: {systemName}
├── Subscription: {systemName}-{ReversedTypeName}
│     a command or query: one per type, shared by every application;
│     rule TypeFilter: sys.Label = '{FullTypeName}', forwards to the handling application's queue (or pool queue)
├── Subscription: {systemName}-{appName}-{ReversedTypeName}
│     an event: one per application and type;
│     rule TypeFilter: sys.Label = '{FullTypeName}', forwards to that application's queue (or pool queue)
Queue: {systemName}-{appName}               the application's commands, queries and events
Queue: {systemName}-{appName}-{poolName}    a pool's types
Queue: {systemName}-{appName}-replies       session-enabled; replies expire after 2 × ResponseTimeout (at least 1 s)
```

`{systemName}` is `ConduitOptions.SystemName` (`conduit` by default), prefixed with `{TopologyPrefix}-` in Developer and Test modes, or whenever `TopologyPrefix` is set explicitly. `{ReversedTypeName}` is the full type name with its segments reversed and joined by `-`: `Orders.Commands.CreateOrder` becomes `CreateOrder-Commands-Orders`. Subscription names are cut to Service Bus's 50 characters, and topic and queue names to 260, with a hash of the whole name standing in for what was cut.

All messages (commands, queries, events) publish to the shared topic, with the full type name as the message's `Subject`. Each subscription has one SQL rule, `sys.Label = '{FullTypeName}'` (`sys.Label` is the SQL filter's name for `Subject`), and forwards what it matches to its queue. Replies are the only exception: they go point-to-point to the session-enabled reply queue.

All creation is idempotent. Multiple pods starting simultaneously will not conflict (409 responses are swallowed).

A subscription that already exists but forwards to a different queue than this application's configuration names is either this application's to redirect or a collision:

- **This application's own.** An event subscription (its name carries the application's name), or a command or query subscription that forwards to another of this application's queues: its app queue or one of its configured pools. That is what moving a type into a pool, or back out of one, leaves behind. Startup updates the subscription's forwarding to the queue the configuration names now and logs `SubscriptionRedirected`.
- **Another application's.** A command or query subscription that forwards to any other queue: a second application handles the same command or query type, which unicast routing forbids. Startup fails with an `InvalidOperationException` (`... This indicates a naming collision between applications. If that queue belonged to this application (a pool it no longer has), delete the subscription.`) rather than take the subscription over.

A command or query subscription that forwards to a pool queue this release does not have is the second case, since that queue is not one of its own. So a release that removes a pool still holding a command or query fails to start until that subscription is deleted, and so does the previous release (a rollback, or an old instance restarting mid-rollout) once a newer one has moved a command or query into a pool the previous release lacks. Delete the subscription, and the next startup creates it afresh. While two releases that both have the queues involved run side by side, the instance that started last decides where the subscription forwards.

An event subscription is always redirected, never refused, because its name carries the application's name. Names are hyphen-joined, though, so two applications can compute the same one: application `a-b` listening for event `X` and application `a` listening for event `X.b` both get `{systemName}-a-b-X`. Rather than fail, each start redirects that shared subscription to its own application's queue, and the other application stops receiving the event until it restarts. Choose application names that cannot produce such a pair.

A `ProducerOnly` host creates only the topic and its reply queue: no application or pool queue, no subscription, and no processor.

Every resource name is sanitized to `[A-Za-z0-9_-]`, any other character becoming `-`. The default `ApplicationName` is the entry assembly's name, so an application named `Conduit.Sample.Orders` gets the queue `conduit-Conduit-Sample-Orders`, not `conduit-Conduit.Sample.Orders`.

### Unicast and multicast subscriptions

Commands and queries use **unicast subscriptions**, named `{systemName}-{ReversedTypeName}`, with no application name. Every application computes the same name for a type, so there is one subscription per type, forwarding to one queue: exactly one application receives each command or query, and a second application that handles the same type fails to start rather than share it (above). The instances of that application compete for the message on its queue.

Events use **multicast subscriptions**, named `{systemName}-{appName}-{ReversedTypeName}`. Each subscribing application gets its own subscription, so every subscriber receives a copy of the event, including the application that published it, since its subscription matches the filter like any other. See [Self-published events](index.md#self-published-events) for how the inbound side avoids running that application's own listeners twice.

### Auto-discovery

The transport reads `ConduitTypeRegistry`, which the source generator fills at compile time, to learn which types this application takes. There is no runtime scan of the container:

- An event type with an `IEventListener<T>` gets a multicast (per-application) subscription.
- A type with an `ICommandHandler<T>`, `ICommandHandler<T,R>` or `IQueryHandler<T,R>` gets a unicast (shared) subscription.

A listener registered at runtime counts too: `AddJobs` (an event trigger's event), `AddAuditListener`
(`AuditBatch`) and a `ConfigureConduitRegistrations` `HandleEvent` override activate their event in the
registry (`ConduitTypeRegistry.ActivateEvent`), so it is subscribed like an event whose listener the
generator found.

No manual subscription configuration is needed.

## Developer and Test modes

`TopologyMode` on `ConduitOptions` controls resource naming and lifetime:

| Mode | Prefix | Durable | Cleanup |
|---|---|---|---|
| `Production` | None, unless `TopologyPrefix` is set | Yes | None |
| `Developer` | `TopologyPrefix`, by default `Environment.UserName` | Yes: Service Bus keeps every entity's messages | Each topic, queue and subscription is deleted after 4 hours idle (`AutoDeleteOnIdle`) |
| `Test` | `TopologyPrefix`, by default `test-` and 8 random hex characters | Yes | Each is deleted after 5 minutes idle |

Developer mode gives each developer isolated broker resources (e.g. topic `alice-conduit` instead of `conduit`). Test mode creates fully isolated, short-lived resources for integration tests.

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.TopologyMode = TopologyMode.Developer;
    conduit.TopologyPrefix = "alice";  // optional, defaults to Environment.UserName

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

When the generated mediator routes a command to an Azure Service Bus endpoint:

1. The transport publishes the command to the shared topic with the `Subject` set to the full type name, `ReplyTo` naming its application's reply queue, and `ReplyToSessionId` set to the request's correlation id.
2. The command's unicast subscription forwards the message to the handling application's queue.
3. The receiving service processes the command and sends a reply to the reply queue, in the session the request named.
4. The sender accepts that session, receives the reply, and deserializes the `DispatchResult`. A failed result throws `RemoteDispatchException` on the caller, carrying the remote error's code and, for an unexpected failure, its support reference.

The reply queue is one per application, shared by its instances; each request's reply is in a session of its own, which only the instance that sent the request accepts, so the reply reaches it however many instances run.

A request with no reply within `ResponseTimeout` (default 60 seconds) fails with a `TimeoutException` (`No reply to '{type}' from endpoint '{endpoint}' within {timeout}.`), the same on every broker transport. It is never an `OperationCanceledException`, which only the caller's own cancellation token produces, so a `catch (OperationCanceledException)` around a dispatch cannot swallow a remote that did not answer. A request still waiting when the transport stops fails at once with an `InvalidOperationException` (`The transport for endpoint '{endpoint}' stopped before the reply arrived.`), rather than waiting out its `ResponseTimeout`.

A reply nobody will read does not stay in the reply queue. The sender reads its reply as soon as it arrives or never (the request timed out, or the sender stopped or crashed), and nothing else accepts a request's session, so each reply carries a time to live of twice the replying endpoint's `ResponseTimeout`, and the reply queue is created with a default message time to live of twice the requesting endpoint's. Service Bus discards a reply when either passes, where it would otherwise stay until the queue reached its size quota and every reply failed. A reply queue that already exists keeps the settings it was created with; the reply's own time to live still applies.

The time to live is never shorter than one second, the least Service Bus accepts, so a `ResponseTimeout` under 500 ms still gives one second. It is always finite: `UseAzureServiceBus` refuses a `ResponseTimeout` that is not positive and finite (see [Request timeouts](index.md#request-timeouts)).

## Failure handling

A broker transport settles each message once, by what became of its dispatch, and never retries it itself: retry happens inside the dispatch ([dispatch retry](../dispatch-retry.md), or a [pipeline behavior](../pipeline-behaviors.md)). The rules every broker shares are in [Failed messages](index.md#failed-messages). On Azure Service Bus:

- **A request whose handler throws** is answered: its caller receives the failure as its reply (`InternalError` with a support reference, or the refusal the handler threw), and the request is completed. An unexpected failure is also captured when the host has Conduit's dead-letter store.
- **An event whose listener throws (or whose dispatch fails outside its listeners: a pipeline behavior throws, a listener cannot be resolved, a gate throws something other than a refusal), or a `[FireAndForget]` command whose handler fails unexpectedly**, has no caller to tell. Where it goes depends on whether the host registered Conduit's [dead-letter queue](../dead-letter-queue.md):
  - **With Conduit's dead-letter store** (`AddDurability(d => d.AddDeadLetterQueue())`), Conduit captures the failed message in its database, with the exception and the message context, and completes it on the broker. The message is browsed and replayed through `IDeadLetters`; a replay is sent straight to the application's queue.
  - **Without one**, the transport abandons the message. Service Bus makes it available again at once and counts the delivery, and once it has been delivered the queue's maximum delivery count times (10, the default the transport leaves in place) it moves the message to the queue's dead-letter subqueue (`{queue}/$deadletterqueue`, reason `MaxDeliveryCountExceeded`). So a listener that always fails runs 10 times in quick succession, then stops. Inspect the dead-letter subqueue (the Azure portal's Service Bus Explorer reads it, for one) and send the message back once the cause is fixed.
- **A message this host cannot read, or one it refuses** (an undispatchable body, a refused event or fire-and-forget command) is completed, and captured first when it is unreadable and the host has the store, as on every broker.

### Stopping

When the host stops, the transport follows the [shared stop sequence](index.md#stopping-a-host-with-work-in-flight). It stops its processors, and a handler still running replies and completes its message before the transport closes them: the transport replies and settles without the processor's cancellation token, which stopping cancels. A message a processor still hands over after the drain began is abandoned undispatched, which Service Bus counts as a delivery.

## Wire format

Messages use JSON serialization (`ConduitRuntime.JsonOptions`) with type information in message properties:

| Property | Value |
|---|---|
| Body | JSON-serialized message payload |
| `Subject` | Full type name (e.g. `"Orders.Commands.CreateOrder"`) |
| `ContentType` | `"application/json"` |
| `MessageId` | From `MessageContext` or auto-generated |
| `CorrelationId` | From `MessageContext.ConversationId` |
| `ReplyTo`, `ReplyToSessionId` | The reply queue and the request's correlation id (requests only); a reply carries the latter as its `SessionId` |
| Application property `CausationId` | From `MessageContext.CausationId` (historical name, kept for existing consumers) |
| Application properties `conduit-message-id`, `conduit-causation-id`, `conduit-conversation-id` | Canonical forms of the same three values, written alongside `MessageId`/`CorrelationId`/the historical `CausationId` property above |
| Application properties `traceparent`, `tracestate` | W3C trace context, when the publish runs inside a trace |
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

This routes `BulkImportCommand` to a dedicated `{systemName}-{appName}-imports` queue processed with concurrency 2, while everything else uses the default queue at concurrency 20. The pool's queue takes all of the pool's types, commands, queries and events alike: their subscriptions forward there instead of to the application's queue. A pool's `Concurrency` defaults to `1`. Use pools to prevent slow or resource-intensive commands from blocking normal traffic.

Moving a type into a pool, out of one, or between two takes effect at the next startup, which redirects the type's existing subscription to its new queue (see [Topology](#topology)). The exception is a command or query whose subscription forwards to a pool queue the starting release does not have (the pool was removed, or a rollback to a release without it): that release fails to start until the subscription is deleted. An event's subscription is redirected regardless.

A pool can be declared in configuration (the endpoint's `Pools` array) and in code under the same name: the two merge into one pool (see [Pool merging](../configuration-reference.md#pool-merging)). The names `replies` and `dead-letter` are reserved.

## Limitations

- **One application per command or query type.** A second application that handles the same type fails to start ([Unicast and multicast subscriptions](#unicast-and-multicast-subscriptions)).
- **Redelivery is immediate.** An abandoned message is delivered again at once, up to the maximum delivery count (10), with no delay between attempts.
- **Five minutes per handler.** The processor renews a message's lock for up to 5 minutes (the SDK's default `MaxAutoLockRenewalDuration`, which the transport leaves in place). A handler that runs longer loses the lock: its completion fails, and Service Bus delivers the message again, counting the delivery.
- **Hyphen-joined names can collide.** Two applications whose names and event types join to the same subscription name take it from each other on every start ([Topology](#topology)).
- **One reply queue per application.** All of an application's instances share `{systemName}-{appName}-replies`, told apart by session.

---

Previous: [RabbitMQ](rabbitmq.md) | [Home](../../README.md) | Next: [Amazon SQS](amazon-sqs.md)
