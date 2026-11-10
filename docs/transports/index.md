# Transports

- [Overview](#overview)
  - [Per-endpoint transport instances](#per-endpoint-transport-instances)
- [Routing](#routing)
- [Shared contracts](#shared-contracts)
  - [\[RemoteContracts\]](#remotecontracts)
- [Event broadcasting](#event-broadcasting)
  - [\[LocalOnly\]](#localonly)
  - [Inbound events](#inbound-events)
  - [Self-published events](#self-published-events)
- [Request timeouts](#request-timeouts)
- [Failed messages](#failed-messages)
- [Stopping a host with work in flight](#stopping-a-host-with-work-in-flight)
- [Developer and Test modes](#developer-and-test-modes)

## Overview

Transports move messages between processes. A handler in one service can handle commands sent from another service, and events published on a server can reach browser clients or downstream services automatically.

Conduit ships six transports in two categories.

**Point-to-point transports** connect two services directly:

- **SignalR** provides persistent bidirectional connections. Browser clients dispatch commands and receive events on the same connection. Services send to each other's hubs with automatic reconnect. See [SignalR](signalr.md).
- **HTTP** maps commands and queries to RESTful endpoints or a wire dispatch endpoint. Request/response only. No server-to-client push. See [HTTP](http.md).

**Broker transports** use a message broker between services. All four share the same
`BrokerTransportBase<TMessage>` template (inbound dispatch orchestration, reply correlation,
failure settlement, the stop sequence, topology naming via `TopologyNaming`/`SystemName`) and differ only in
broker-specific primitives, so they follow identical Conduit patterns. Each has a dedicated `IConduitTransport` and
an `IHostedService` consumer, and each guide follows the same outline: setup, options and configuration keys,
topology, Developer and Test modes, request/response, failure handling, wire format, receiver pools and limitations.

| Transport | Broker / protocol | Guide |
|---|---|---|
| **RabbitMQ** | RabbitMQ.Client (AMQP 0-9-1 exchanges, queues, bindings) | [rabbitmq.md](rabbitmq.md) |
| **Azure Service Bus** | Azure.Messaging.ServiceBus (queues, topics, subscriptions) | [azure-service-bus.md](azure-service-bus.md) |
| **Amazon SQS** | AWSSDK SQS (+ SNS for fan-out) | [amazon-sqs.md](amazon-sqs.md) |
| **ActiveMQ** | AMQPNetLite.Core (AMQP 1.0, ActiveMQ Artemis) | [activemq.md](activemq.md) |

Your handlers never reference a transport. The same `ICommandHandler<T>` works whether the message arrived locally, over SignalR, via HTTP, or from a broker queue.

### Per-endpoint transport instances

Every endpoint gets its own transport instance, options, and `IHostedService` lifecycle, keyed in DI
by the endpoint name. Two endpoints on the same transport package (say `orders` pointed at one
RabbitMQ cluster and `audit` pointed at another) are two fully independent connections, each built
from its own options. This is true for all six transports. No broker package registers its options or its
concrete transport type without a key: the only non-keyed entries are the `IConduitTransport` and
`IHostedService` forwarders, one per endpoint, each resolving that endpoint's keyed instance. Dead-letter replay
resolves the transport by the endpoint name it recorded, so a replay reaches the broker the message came from.

The intended use of two endpoints on the same transport type is **two different brokers**. Two
endpoints on the same transport pointed at the *same* broker from the *same* application start two
independent transports, each creating its own app-scoped topology, and that topology collides:
`TopologyNaming.CreateAppResourceName` keys resource names off `ApplicationName`, not the
endpoint name, so both endpoints derive identical resource names, with different consequences for
each message kind. Commands and queries route: dispatch resolves exactly one keyed transport by
endpoint name, so a routed message is only ever sent once, and the two endpoints' identical queue
names just make them competing consumers on that one queue: redundant, not incorrect. Events do not
route, they broadcast: the generated mediator sends each event to every registered `IConduitTransport`,
with no dedupe by broker or connection. Two endpoints on the same broker publish every event twice to the
same topic, and because both derive the same app-scoped event queue name, that queue receives two copies
and listeners run twice, unless the durability inbox is enabled to dedupe by message ID. If you need two
logically separate destinations on the same broker technology, give them distinct connection strings
(different brokers, or at minimum different virtual hosts/namespaces).

Configuration can supply each endpoint's operational values (connection strings, regions, timeouts)
without touching code. See [Loading from IConfiguration](../configuration-reference.md#loading-from-iconfiguration).

## Routing

By default, all messages dispatch locally. To send commands and queries to a remote service, define an endpoint and a route:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("inventory", endpoint =>
    {
        endpoint.UseSignalR(new Uri("https://inventory-service/conduit"));
        endpoint.RouteNamespace<Inventory.Commands.ReserveStock>();
    });
});
```

`AddEndpoint` defines a named destination and configures its transport. `RouteNamespace<T>()` tells the generated mediator to send any command or query in `T`'s namespace, or in a namespace beneath it, to that endpoint. `RouteType<T>()` routes exactly one type. Routes are checked in the order they were declared and the first match wins.

The calling code uses `IMediator` identically to local dispatch:

```csharp
bool reserved = await mediator.ExecuteAsync(
    new ReserveInventoryCommand("SKU-42", 5), cancellationToken);
```

The generated mediator checks the route table, serializes the command into a `DispatchEnvelope`, sends it over the transport, and deserializes the response. The caller never knows the handler is in another process. A remote failure throws `RemoteDispatchException`, carrying the remote error's code.

Routes only apply to commands and queries. Events broadcast rather than route (see below).

## Shared contracts

When two services communicate, both need the message type definitions. The simplest approach is to copy the record declarations into the calling service. They just need to match by full type name and shape.

For larger systems, put the shared types in a standalone class library that both services reference. The `Conduit.NET.Contracts` package holds the message interfaces and attributes with no package dependencies of its own:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Conduit.NET.Contracts" />
  </ItemGroup>
</Project>
```

```csharp
namespace Inventory.Commands;

public record ReserveInventoryCommand(string ProductId, int Quantity) : ICommand<bool>;
```

Both services reference the contracts library. The handling service implements handlers. The calling service configures routes. A contract that carries a file (`ConduitFile`) needs the `Conduit.NET` package instead, which holds that type.

The namespace matters. A namespace route matches on whole namespace segments: `RouteNamespace<Inventory.Commands.ReserveStock>()` covers `Inventory.Commands` and `Inventory.Commands.Bulk`, but not `Inventory.CommandsArchive`. Keep contract types in a predictable hierarchy.

### [RemoteContracts]

The source generator produces a `CONDUIT001` warning for any command or query with no local handler. In the calling service, this is expected since the contracts are handled remotely.

```csharp
[assembly: RemoteContracts("Inventory.Commands")]
```

This suppresses the warning for all types in that namespace and the namespaces beneath it, matched on
whole segments (`"Inventory"` covers `Inventory.Commands`, not `InventoryAudit`). Add one per remote
namespace:

```csharp
[assembly: RemoteContracts("Inventory.Commands")]
[assembly: RemoteContracts("Billing.Commands")]
```

The attribute only affects diagnostics. It does not change routing behavior.

## Event broadcasting

`PublishAsync` does two things:

1. Dispatches to all local `IEventListener<T>` implementations.
2. Broadcasts to every `IConduitTransport` registered in DI.

```
PublishAsync(event)
    |
    +-- local listeners (in-process)
    |       SendConfirmation.HandleAsync()
    |       UpdateAnalytics.HandleAsync()
    |
    +-- transport broadcast (out-of-process)
            SignalRTransport.SendEventAsync()  --> connected clients ([ClientVisible] events, by default)
            ServiceBusTransport.SendEventAsync() --> topic subscribers
```

The publisher does not know who is listening. Each transport receives a `DispatchEnvelope` and delivers it using its own mechanism.

The local listeners run first (a `[FireAndForget]` event's in the background), then each transport is sent the event in turn. A transport that throws is logged (`TransportBroadcastFailed`, naming the transport and the event) and the broadcast carries on to the next: a transport failure never fails the publish. A local listener that throws does (unless the event is `[FireAndForget]`, whose listeners' failures are logged): its failure reaches the publisher before anything is broadcast. Only server-mode and broker transports are broadcast to; a client-mode SignalR or HTTP endpoint is not.

### [LocalOnly]

Some events must never leave the process. Mark them with `[LocalOnly]`:

```csharp
using Conduit.Attributes;
using Conduit.Messages;

[LocalOnly]
public record UserPasswordChanged(Guid UserId) : IEvent;
```

The source generator decides at compile time which events may be broadcast: every event type it discovered that is not `[LocalOnly]`. The attribute is inherited, so an event deriving from a `[LocalOnly]` event is local-only too, and the gate fails closed: an event type the generator did not discover (a private or file-local type, say) is never broadcast. Use this for PII events, security events, and internal coordination.

### Inbound events

Events arriving from external sources dispatch to local listeners only. They are never re-broadcast to transports.

```
Browser --SignalR--> ConduitHub.Publish()
                         |
                         v
                    PublishLocalAsync(event)
                         |
                         +-- local listeners only
                              (no transport broadcast)
```

This prevents echo loops. If an inbound event were re-broadcast, it would bounce between services indefinitely. The generated mediator uses a private `PublishLocalAsync` method for wire-inbound events that skips broadcast entirely.

### Self-published events

The four broker transports (RabbitMQ, Azure Service Bus, Amazon SQS, ActiveMQ) route every
message through a shared topic/exchange, and each application's event queue is bound to every event type
the application listens for, including the ones it publishes itself. Left alone,
that means a broker-backed application receives its own broadcast back:

```
App A publishes MyEvent
  |
  +-- local listeners run inline, on A                    (PublishAsync, always)
  |
  +-- transport broadcast --> topic --> A's own per-app queue --> A receives MyEvent again
```

Running A's listeners a second time would be a bug, not a feature, so every outbound event carries
the publishing application's name (`ConduitOptions.ApplicationName`, as `conduit-source-app`), and a
broker transport drops an inbound event whose stamp matches its own application name: that
application's listeners already ran when it was published. The drop happens before a DI scope is created or the payload is
deserialized, and is recorded as a `SelfPublishedEventSkipped` observation, visible in the
[audit trail](../audit.md), so "why didn't my listener run" has an answer.

A few things worth knowing about the check:

- **It keys on the application, not the instance.** Every replica of a service shares the same
  `ApplicationName`, so any instance of application A drops A's own copy, not just the instance
  that published it. An instance-scoped stamp would let a *peer* instance run the listeners again,
  the defect this exists to prevent, one hop later.
- **`ApplicationName` must be unique per logical application.** The stamp *is* the application's
  identity, so two genuinely different deployments that happen to share one `ApplicationName` are
  a single application as far as the filter is concerned: each drops the other's events, and an
  event that one publishes but only the other listens for is lost with no error. Sharing a name
  is unsupported anyway (it also means sharing the application's queues, so the two deployments
  would steal each other's messages), and the filter makes that misconfigured
  case fail more quietly. Give every logical application its own name; give every replica of one
  application the same name. The names must also differ **after sanitizing**: a broker resource
  name keeps only `[A-Za-z0-9_-]`, every other character becoming `-`, so `Orders.Api` and
  `Orders-Api` carry different stamps but name the same queues, and share them as one application would.
- **Requests are never dropped.** A command or query legitimately routed back to its own
  application (self-routing, or a receiver pool shared with the publisher) must still be
  handled, so the check only ever looks at events.
- **Event-ness is decided by the type registry, not by "not a request."** A routed
  `[FireAndForget]` command also arrives expecting no reply, which looks like an event at the wire
  level. Checking the type registry instead of the absence of a reply keeps such a command from
  being dropped.
- **An application with no configured name never filters.** A missing stamp would otherwise
  compare equal to a missing `ApplicationName`, and every inbound event would be silently
  discarded: an outage, not a fix. The guard exists specifically to rule that out.
- **A missing or foreign stamp always dispatches normally**, so an event from a non-Conduit
  producer, or from a host with no `ApplicationName`, is handled like any other.
- **SignalR and HTTP are unaffected.** SignalR's server mode never consumes its own hub, and its
  client mode dispatches to the server's local listeners without re-broadcasting to other clients.
  HTTP never hands a host back its own event: `/publish` only receives what another host sends it. The filter lives entirely in
  `BrokerTransportBase`, shared by the four broker transports only.
- **There is no configuration knob to turn it off.** The filter is unconditional wherever it
  applies.

One consequence follows directly: an application's own listeners always run **on the publishing
instance**, not load-balanced across replicas, because the inline run happens at publish time and
the broker-delivered copy is the one that gets dropped. See
[Core Concepts → Where an application's own listeners run](../core-concepts.md#where-an-applications-own-listeners-run).

## Request timeouts

A request sent through a broker transport has one deadline, its endpoint's `ResponseTimeout` (default
60 seconds), and it covers the publish and the wait for the reply together. When it passes, the request
fails with `TimeoutException` (`No reply to '{type}' from endpoint '{endpoint}' within {timeout}.`),
whether the reply never came or the broker never confirmed the publish (RabbitMQ waits for every
publish's confirm, which a broker under a memory or disk alarm withholds). It is never an
`OperationCanceledException`: only the caller's own cancellation token produces one. A request still
waiting when the transport stops fails with an `InvalidOperationException`.

`ResponseTimeout` must be positive and finite: zero, a negative value, `Timeout.InfiniteTimeSpan` and
anything longer than about 49.7 days (the longest delay a .NET timer takes) fail the endpoint's
registration with an `InvalidOperationException` naming the endpoint, rather than every request later.

## Failed messages

A broker transport settles each message it receives once, by what became of its dispatch. The
transport never retries a failed message itself: retry happens inside the dispatch, before the transport
sees the failure ([dispatch retry](../dispatch-retry.md), or a Polly pipeline from
[Resilience](../resilience.md)).

| What happened | With a [dead-letter queue](../dead-letter-queue.md) | Without one |
|---|---|---|
| Handled | Acknowledged | Acknowledged |
| A request (it carries a reply address) failed or was refused | The failure is its reply; an unexpected failure (`InternalError`) is also captured | Acknowledged after the reply |
| An event listener threw, or a `[FireAndForget]` command's handler failed unexpectedly (`InternalError`): no caller is waiting to hear it | Captured, then acknowledged | Abandoned, so the broker's own dead-lettering applies (see below) |
| A message this host cannot read: not JSON, an unknown type, a body its type cannot read (`ContractMismatch`), or any other `BadRequest` refusal of an event or `[FireAndForget]` command, a validation failure among them | Captured, then acknowledged | Acknowledged: a redelivery would fail the same way |
| An event or `[FireAndForget]` command was refused otherwise (the type filter's `Forbidden`, `NotFound`, `Conflict`, any other `ConduitException`) | Acknowledged, not captured | Acknowledged: a redelivery would be refused again |

An event whose dispatch fails outside its listeners (a pipeline behavior throws, a listener cannot be resolved, or
a gate throws an exception that is not a `ConduitException`) is settled as a listener failure is, captured or else
abandoned. A publisher over HTTP `/publish` is answered the `InternalError` envelope with its support reference, and
one over the SignalR hub's `Publish` a `HubException` with the fixed message `An unexpected error occurred.`

An abandoned message is not gone, and it is not handled only once: until the broker dead-letters it, the broker
delivers it again and its listener or handler runs again.

- [RabbitMQ](rabbitmq.md#failure-handling) rejects it once, without requeue, to the system's dead-letter queue: the
  handler ran once.
- [Azure Service Bus](azure-service-bus.md#failure-handling) redelivers it at once, up to the entity's maximum
  delivery count (10 by default), then moves it to the entity's dead-letter subqueue.
- [Amazon SQS](amazon-sqs.md#failure-handling) delivers it again a visibility timeout later, up to `MaxReceiveCount`
  (5 by default), then the redrive policy moves it to the queue's dead-letter queue.
- [ActiveMQ](activemq.md#failure-handling) redelivers it up to the address's `max-delivery-attempts` (10 by
  default), with no delay between attempts by default, then moves it to the dead-letter address.

So a listener or a `[FireAndForget]` handler with side effects that can fail part-way should be idempotent, or
the host should configure Conduit's [dead-letter queue](../dead-letter-queue.md), so the first failure is captured
and the broker does not redeliver it. The [inbox](../inbox.md) does not stop these repeats: it discards a
redelivery only of a dispatch that succeeded, and releases a failed one so it runs again.

A refused `[FireAndForget]` command is logged once at Warning (`FireAndForgetCommandRefused`), naming its type
and the refusal's code, since nothing else will report it. A dead-letter replay of a command or query arrives
without a reply address too, so it is settled as a `[FireAndForget]` command is.

SignalR and HTTP settle nothing: a failure is answered to the caller, or, for an event published over them,
fails the call ([SignalR → Failure handling](signalr.md#failure-handling), [HTTP → Failure handling](http.md#failure-handling)).

## Stopping a host with work in flight

When the host stops (SIGTERM, `StopAsync`), a broker transport finishes the messages it is already
handling before it closes anything those handlers need:

1. **Stop receiving.** RabbitMQ cancels its consumers, ActiveMQ stops replenishing its links' credit
   (Artemis may still push up to each link's prefetch, [see ActiveMQ](activemq.md#stopping)),
   Amazon SQS stops polling, and Azure Service Bus stops its processors. The channels, links and
   clients stay open, and so does the reply consumer, since a handler may itself be awaiting a reply.
   A transport that is reconnecting after a lost connection stops reconnecting.
2. **Drain.** The transport waits for every handler still running to finish, reply and acknowledge
   its message, bounded by the host's shutdown deadline (`HostOptions.ShutdownTimeout`, 30 seconds
   by default). A message the broker client still hands over during this step is returned
   undispatched, for another instance to take (on RabbitMQ, requeued; on ActiveMQ, released; on Amazon
   SQS, made visible again at once, as is a message received but not yet started when polling stopped, and
   one SQS does not take back is logged and reappears when its visibility timeout lapses; on Azure Service
   Bus, abandoned, which counts as a delivery): it is not a failure, so it is never dead-lettered. A handler
   that fails during the drain is abandoned like any other failure (on RabbitMQ, dead-lettered; on ActiveMQ,
   counted as a delivery attempt; on Amazon SQS, delivered again once its visibility timeout lapses, counted
   toward `MaxReceiveCount`).
3. **Close.** Only then are the channels, links and receivers closed, and a request still awaiting
   a reply fails with an `InvalidOperationException`.

So a handler that finishes within the deadline runs once and its message is acknowledged once:
stopping does not cause a redelivery. A handler still running at the deadline is left behind, not
cancelled (handlers receive no shutdown token): it may run on until the process exits, and unless its
acknowledgement still reaches the broker, the broker redelivers its message, so it may run twice
(logged as `DrainAbandoned`, a Warning naming how many). If your handlers can outlast the deadline,
raise `HostOptions.ShutdownTimeout` to cover them, or make them idempotent with the
[inbox](../inbox.md).

## Developer and Test modes

`ConduitOptions.TopologyMode` names a broker's resources and decides how long they live. HTTP and SignalR create no
broker resources, so the mode does not apply to them.

| Mode | Prefix by default | Lifetime |
|---|---|---|
| `Production` | none | Durable; nothing is deleted. |
| `Developer` | `Environment.UserName` | Ephemeral, where the broker can delete a resource itself. |
| `Test` | `test-` and 8 random hex characters | As Developer, sooner. |

Developer mode gives each developer isolated resources on a shared broker: under the prefix `alice`, every name Conduit
gives a resource starts with `alice-` (RabbitMQ's exchange is `alice-conduit` instead of `conduit`). Test mode gives
every run a prefix of its own and, where the broker can delete them, short-lived resources, so integration tests that
share a broker never see each other's messages. `TopologyPrefix` overrides the default prefix, in Production too; give every host of a multi-host test the
same one so they share one topology ([ConduitBuilder properties](../configuration-reference.md#conduitbuilder-properties)).
Each broker guide's "Developer and Test modes" section lists its resources' durability and cleanup in each mode.

<a id="ephemeral-resource-cleanup"></a>

How each broker removes Developer and Test resources:

| Transport | Developer | Test |
|---|---|---|
| RabbitMQ | Non-durable and auto-delete: a queue goes when its last consumer leaves, an exchange when its last binding goes; except the system dead-letter queue, which is never auto-delete (non-durable, so it lives until the broker restarts) | As Developer, and the dead-letter queue expires once unused for 5 minutes |
| Azure Service Bus | Each topic, queue and subscription is deleted after 4 hours idle (`AutoDeleteOnIdle`) | After 5 minutes idle |
| ActiveMQ | A queue is deleted 4 hours after its last consumer left; the two addresses stay | 5 minutes; the addresses stay |
| Amazon SQS | Nothing is deleted: SNS and SQS have no auto-delete | Nothing is deleted |

ActiveMQ's two addresses per prefix stay behind empty, because Artemis does not auto-delete an address created
through its management API ([see ActiveMQ](activemq.md#developer-and-test-modes)). For Amazon SQS the transport
library exposes `AmazonSqsTopologyReaper.ReapByPrefixAsync(options, prefix)`, a best-effort cleanup that deletes the
topics and queues whose names start with a prefix. Conduit's own test suites run it once per test project, scoped to a
stable per-project prefix, so a shared or live broker doesn't accumulate test resources; an application that names
resources under a known prefix can run the same cleanup. Developer-mode resources are not reaped for SQS by
themselves: run that broker in a container locally, or call the reaper yourself.

---

Previous: [Core Concepts](../core-concepts.md) | [Home](../../README.md) | Next: [RabbitMQ](rabbitmq.md)
