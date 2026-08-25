# Transports

- [Overview](#overview)
- [Routing](#routing)
- [Shared contracts](#shared-contracts)
  - [\[RemoteContracts\]](#remotecontracts)
- [Event broadcasting](#event-broadcasting)
  - [\[LocalOnly\]](#localonly)
  - [Inbound events](#inbound-events)
  - [Self-published events](#self-published-events)

## Overview

Transports move messages between processes. A handler in one service can handle commands sent from another service, and events published on a server can reach browser clients or downstream services automatically.

Conduit ships seven transports in two categories.

**Point-to-point transports** connect two services directly:

- **SignalR** provides persistent bidirectional connections. Browser clients dispatch commands and receive events on the same connection. Services connect to each other with automatic reconnect. See [SignalR](signalr.md).
- **HTTP** maps commands and queries to RESTful endpoints or an envelope dispatch endpoint. Request/response only. No server-to-client push. See [HTTP](http.md).

**Durable messaging (broker) transports** use a message broker for reliable communication. All five
share the same `BrokerTransportBase<TMessage>` template (inbound dispatch orchestration, reply
queues, topology naming via `TopologyNaming`/`SystemName`) and differ only in broker-specific
primitives, so they follow identical Conduit patterns. Each has a dedicated `IConduitTransport` and
an `IHostedService` consumer:

| Transport | Broker / protocol | Guide |
|---|---|---|
| **Azure Service Bus** | Azure.Messaging.ServiceBus (queues, topics, subscriptions) | [azure-service-bus.md](azure-service-bus.md) |
| **RabbitMQ** | RabbitMQ.Client (AMQP exchanges, queues, bindings) | [rabbitmq.md](rabbitmq.md) |
| **Kafka** | Confluent.Kafka (topics, partitions) | [`src/Conduit.Transport.Kafka/CLAUDE.md`](../../src/Conduit.Transport.Kafka/CLAUDE.md) |
| **Amazon SQS** | AWSSDK SQS (+ SNS for fan-out) | [`src/Conduit.Transport.AmazonSqs/CLAUDE.md`](../../src/Conduit.Transport.AmazonSqs/CLAUDE.md) |
| **ActiveMQ** | AMQPNetLite.Core (ActiveMQ Artemis) | [`src/Conduit.Transport.ActiveMQ/CLAUDE.md`](../../src/Conduit.Transport.ActiveMQ/CLAUDE.md) |

Kafka, Amazon SQS, and ActiveMQ do not yet have dedicated prose guides; their per-project
`CLAUDE.md` files document configuration and broker-specific behavior. They are configured the same
way as the documented broker transports — `endpoint.UseKafka(...)`, `endpoint.UseAmazonSqs(...)`,
`endpoint.UseActiveMQ(...)`.

Your handlers never reference a transport. The same `ICommandHandler<T>` works whether the message arrived locally, over SignalR, via HTTP, or from a broker queue.

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

`AddEndpoint` defines a named destination and configures its transport. `RouteNamespace<T>()` tells the generated mediator to send any command or query whose namespace matches `T`'s namespace to that endpoint.

The calling code uses `IMediator` identically to local dispatch:

```csharp
bool reserved = await mediator.ExecuteAsync(
    new ReserveInventoryCommand("SKU-42", 5), cancellationToken);
```

The generated mediator checks the route table, serializes the command into a `DispatchEnvelope`, sends it over the transport, and deserializes the response. The caller never knows the handler is in another process.

Routes only apply to commands and queries. Events broadcast rather than route (see below).

## Shared contracts

When two services communicate, both need the message type definitions. The simplest approach is to copy the record declarations into the calling service. They just need to match by full type name and shape.

For larger systems, put the shared types in a standalone class library that both services reference:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Conduit" />
  </ItemGroup>
</Project>
```

```csharp
namespace Inventory.Commands;

public record ReserveInventoryCommand(string ProductId, int Quantity) : ICommand<bool>;
```

Both services reference the contracts library. The handling service implements handlers. The calling service configures routes.

The namespace matters. Routes match by namespace prefix, so keep contract types in a predictable hierarchy.

### [RemoteContracts]

The source generator produces a `CONDUIT001` warning for any command or query with no local handler. In the calling service, this is expected since the contracts are handled remotely.

```csharp
[assembly: RemoteContracts("Inventory.Commands")]
```

This suppresses the warning for all types in that namespace. Add one per remote namespace:

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
            SignalRTransport.SendEventAsync()  --> connected clients
            ServiceBusTransport.SendEventAsync() --> topic subscribers
```

The publisher does not know who is listening. Each transport receives a `DispatchEnvelope` and delivers it using its own mechanism.

Transport broadcast is fire-and-forget. A transport failure does not affect local listener execution.

### [LocalOnly]

Some events must never leave the process. Mark them with `[LocalOnly]`:

```csharp
using Conduit.Attributes;
using Conduit.Messages;

[LocalOnly]
public record UserPasswordChanged(Guid UserId) : IEvent;
```

The source generator omits the transport broadcast call at compile time. There is no runtime check. Use this for PII events, security events, and internal coordination.

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

The five broker transports (Azure Service Bus, RabbitMQ, Kafka, Amazon SQS, ActiveMQ) route every
message through a shared topic/exchange, and each application's per-app event queue is bound to
*every* discovered event type — including the ones the application publishes itself. Left alone,
that means a broker-backed application receives its own broadcast back:

```
App A publishes MyEvent
  |
  +-- local listeners run inline, on A                    (PublishAsync, always)
  |
  +-- transport broadcast --> topic --> A's own per-app queue --> A receives MyEvent again
```

Running A's listeners a second time would be a bug, not a feature, so every outbound event carries
the publishing application's name (`ConduitOptions.ApplicationName`), and a broker transport drops
an inbound event whose stamp matches its own application name — that application's listeners
already ran when it was published. The drop happens before a DI scope is created or the payload is
deserialized, and is recorded as a `SelfPublishedEventSkipped` observation, visible in the
[audit trail](../audit.md), so "why didn't my listener run" has an answer.

A few things worth knowing about the check:

- **It keys on the application, not the instance.** Every replica of a service shares the same
  `ApplicationName`, so any instance of application A drops A's own copy, not just the instance
  that published it. An instance-scoped stamp would let a *peer* instance run the listeners again
  — the defect this exists to prevent, one hop later.
- **Requests are never dropped.** A command or query legitimately routed back to its own
  application — self-routing, or a receiver pool shared with the publisher — must still be
  handled, so the check only ever looks at events.
- **Event-ness is decided by the type registry, not by "not a request."** A routed
  `[FireAndForget]` command also arrives expecting no reply, which looks like an event at the wire
  level. Checking the type registry instead of the absence of a reply keeps such a command from
  being dropped.
- **An application with no configured name never filters.** A missing stamp would otherwise
  compare equal to a missing `ApplicationName`, and every inbound event would be silently
  discarded — an outage, not a fix. The guard exists specifically to rule that out.
- **A missing or foreign stamp always dispatches normally**, so events published before an
  upgrade (no stamp yet) and events from non-Conduit producers behave exactly as before. This
  keeps a rolling upgrade safe.
- **SignalR and HTTP are unaffected.** SignalR's server mode never consumes its own hub, and its
  client mode dispatches to the server's local listeners without re-broadcasting to other clients.
  HTTP carries no events at all — `SendEventAsync` is unsupported. The filter lives entirely in
  `BrokerTransportBase`, shared by the five broker transports only.
- **There is no configuration knob to turn it off.** The filter is unconditional wherever it
  applies; add an opt-out only if a concrete use case for double-processing your own events
  actually shows up.

One consequence follows directly: an application's own listeners always run **on the publishing
instance**, not load-balanced across replicas, because the inline run happens at publish time and
the broker-delivered copy is the one that gets dropped. See
[Core Concepts → Where an application's own listeners run](../core-concepts.md#where-an-applications-own-listeners-run).

## Ephemeral resource cleanup

RabbitMQ, Azure Service Bus, and ActiveMQ auto-delete ephemeral resources via native broker TTLs.
Kafka and SQS have no equivalent, so the transport libraries expose `KafkaTopologyReaper.ReapByPrefixAsync`
and `AmazonSqsTopologyReaper.ReapByPrefixAsync` — best-effort cleanup that deletes resources whose names
start with a given prefix. Conduit's own test suites use this (once per test project, scoped to a stable
per-project prefix) so a shared or live broker doesn't accumulate test resources. Applications that name
resources under a known prefix can run the same cleanup. Developer-mode resources are not auto-reaped for
Kafka/SQS — run those brokers in a container locally, or call the reaper yourself.

---

Previous: [Core Concepts](../core-concepts.md) | [Home](../../README.md) | Next: [SignalR](signalr.md)
