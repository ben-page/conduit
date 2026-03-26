# Transports

- [Overview](#overview)
- [Routing](#routing)
- [Shared contracts](#shared-contracts)
  - [\[RemoteContracts\]](#remotecontracts)
- [Event broadcasting](#event-broadcasting)
  - [\[LocalOnly\]](#localonly)
  - [Inbound events](#inbound-events)

## Overview

Transports move messages between processes. A handler in one service can handle commands sent from another service, and events published on a server can reach browser clients or downstream services automatically.

Conduit ships four transports in two categories.

**Point-to-point transports** connect two services directly:

- **SignalR** provides persistent bidirectional connections. Browser clients dispatch commands and receive events on the same connection. Services connect to each other with automatic reconnect.
- **HTTP** maps commands and queries to RESTful endpoints or an envelope dispatch endpoint. Request/response only. No server-to-client push.

**Durable messaging transports** use a broker for reliable communication:

- **Azure Service Bus** provides queues, topics, and subscriptions with persistence, retry, and dead-lettering.
- **RabbitMQ** provides exchanges, queues, and bindings with similar durability using the AMQP protocol.

Your handlers never reference a transport. The same `ICommandHandler<T>` works whether the message arrived locally, over SignalR, via HTTP, or from a service bus queue.

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

---

Previous: [Core Concepts](../core-concepts.md) | [Home](../../README.md) | Next: [SignalR](signalr.md)
