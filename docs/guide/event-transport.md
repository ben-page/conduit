# Event Transport

## Basic event publishing

```csharp
using Conduit.Messages;

public record OrderPlaced(Guid OrderId, decimal Total) : IEvent;
```

```csharp
using Conduit.Handlers;

public class SendOrderConfirmation : IEventListener<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced @event, CancellationToken cancellationToken)
    {
        // Send confirmation email to the customer
        return Task.CompletedTask;
    }
}

public class UpdateInventoryCounts : IEventListener<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced @event, CancellationToken cancellationToken)
    {
        // Decrement stock for each item in the order
        return Task.CompletedTask;
    }
}
```

```csharp
await mediator.PublishAsync(new OrderPlaced(orderId, 59.99m), cancellationToken);
```

Both `SendOrderConfirmation` and `UpdateInventoryCounts` run for the same event. They execute independently with continue-on-failure semantics: if `SendOrderConfirmation` throws, `UpdateInventoryCounts` still runs. No listener can block or cancel another.

The source generator discovers all `IEventListener<T>` implementations automatically. No registration attributes needed.

## Broadcast to transports

`PublishAsync` does two things:

1. Dispatches to all local `IEventListener<T>` implementations in the current process.
2. Broadcasts to every `IConduitTransport` registered in DI.

```
PublishAsync(event)
    |
    +-- local listeners (in-process)
    |       SendOrderConfirmation.HandleAsync()
    |       UpdateInventoryCounts.HandleAsync()
    |
    +-- transport broadcast (out-of-process)
            SignalRTransport.SendEventAsync()  --> connected clients
            HttpTransport.SendEventAsync()     --> remote service
```

The producer does not know who is listening. It publishes the event and Conduit handles delivery. Each registered `IConduitTransport` receives a `DispatchEnvelope` containing the event's full type name and serialized payload. The transport decides how to deliver it (SignalR pushes to connected clients, HTTP posts to a remote endpoint).

Transport broadcast is fire-and-forget. A transport failure does not affect local listener execution -- local listeners always run first.

## `[LocalOnly]`

Some events must never leave the process. Mark them with `[LocalOnly]` to prevent transport broadcast entirely.

```csharp
using Conduit.Attributes;
using Conduit.Messages;

[LocalOnly]
public record UserPasswordChanged(Guid UserId) : IEvent;
```

```csharp
public class InvalidateUserSessions : IEventListener<UserPasswordChanged>
{
    public Task HandleAsync(UserPasswordChanged @event, CancellationToken cancellationToken)
    {
        // Invalidate all active sessions for this user
        return Task.CompletedTask;
    }
}
```

```csharp
await mediator.PublishAsync(new UserPasswordChanged(userId), cancellationToken);
// Dispatches to InvalidateUserSessions only. Never hits any transport.
```

The source generator reads `[LocalOnly]` at compile time and omits the broadcast code path from the generated `PublishAsync`. There is no runtime check -- the broadcast call is simply not emitted.

Use `[LocalOnly]` for:

- **PII events** -- user data that must not cross service boundaries
- **Security events** -- password changes, failed login attempts, session invalidation
- **Internal state events** -- cache invalidation, local coordination between components

## Inbound events

Events arriving from external sources (a browser client via SignalR, another service via HTTP) dispatch to local listeners only. They are never re-broadcast to transports.

```
Browser --SignalR--> ConduitHub.Publish()
                         |
                         v
                    PublishLocalAsync(event)
                         |
                         +-- local listeners only
                         |       (no transport broadcast)
```

This prevents echo loops. If an event arrived over SignalR and `PublishAsync` broadcast it back out to transports, the same event would bounce between services indefinitely. The generated mediator uses a private `PublishLocalAsync` method for wire-inbound events that skips the transport broadcast step entirely.

The same applies to HTTP-inbound events. The inbound endpoint on each transport calls `HandleWireEventAsync` on `IWireMediator`, which deserializes the event and calls `PublishLocalAsync` -- never `PublishAsync`.

Next: [SignalR Transport](signalr-transport.md)
