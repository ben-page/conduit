# SignalR Transport

## Setup

```csharp
builder.Services.AddConduitSignalR();
```

```csharp
app.MapHub<ConduitHub>("/hub");
```

`AddConduitSignalR()` does three things: calls `AddSignalR()` on the service collection, registers `SignalRTransport` as a singleton, and registers that same instance as `IConduitTransport`. The transport starts in server mode -- it holds an `IHubContext<ConduitHub>` and pushes events to connected clients.

`MapHub<ConduitHub>` maps the inbound SignalR hub. The hub exposes two methods to clients: `Dispatch` (commands/queries) and `Publish` (events).

Full server setup from the Notes sample:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddConduit();
builder.Services.AddConduitSignalR();

var app = builder.Build();
app.MapHub<ConduitHub>("/hub");
app.Run();
```

## Bidirectional events

SignalR gives you a single connection that carries traffic in both directions.

**Client to server:** Commands and queries go through the hub's `Dispatch` method. The hub resolves `IWireMediator` from DI and delegates to `HandleWireRequestAsync`. Events go through `Publish`, which delegates to `HandleWireEventAsync`.

**Server to client:** When your handler calls `mediator.PublishAsync(event)`, the generated mediator dispatches to local listeners and then broadcasts to every registered `IConduitTransport`. The server-mode `SignalRTransport` calls `hubContext.Clients.All.SendAsync(type, payload)`, pushing the event to all connected clients over their existing connections.

No separate subscription step is needed. Clients receive events by listening for the event's full type name on the connection:

```typescript
// TypeScript (browser)
conduit.on('MyApp.Events.OrderShippedEvent', (event) => {
  console.log('Shipped:', event.orderId);
});
```

```csharp
// C# (SignalR client)
hubConnection.On<JsonElement>("MyApp.Events.OrderShippedEvent", payload =>
{
    Console.WriteLine($"Shipped: {payload}");
});
```

## Solicited events

The most common pattern: send a command, receive the resulting event on the same connection.

```csharp
// Server: handler publishes an event after processing
public class CreateNoteHandler(NoteStore store, IMediator mediator)
    : ICommandHandler<CreateNoteCommand, Guid>
{
    public async Task<Guid> HandleAsync(
        CreateNoteCommand command, CancellationToken cancellationToken)
    {
        var id = store.Add(command.Text);
        await mediator.PublishAsync(
            new NoteCreatedEvent(id, command.Text), cancellationToken);
        return id;
    }
}
```

```typescript
// Browser: subscribe to the event, then send the command
conduit.on('Conduit.Sample.Notes.Events.NoteCreatedEvent', (event) => {
  console.log('Created:', event.id, event.text);
});

const result = await conduit.execute(
  'Conduit.Sample.Notes.Commands.CreateNoteCommand',
  { text: 'Buy groceries' }
);
```

The flow:

1. Browser sends `CreateNoteCommand` via the hub's `Dispatch` method.
2. Server handles the command, calls `PublishAsync(new NoteCreatedEvent(...))`.
3. `PublishAsync` dispatches to local listeners, then the server-mode `SignalRTransport` broadcasts the event via `Clients.All.SendAsync`.
4. Browser receives `NoteCreatedEvent` on the same connection.
5. The `Dispatch` call also returns the command result (`Guid`) as a `DispatchResult<string>`.

The command response and the event arrive independently. The response comes back as the return value of `Dispatch`. The event arrives as a push on the connection. Your code handles them separately.

## Client mode (service-to-service)

For C# services that need to send commands to a remote SignalR hub, configure an endpoint with `UseSignalR`:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("inventory", endpoint =>
        endpoint.UseSignalR(new Uri("https://localhost:5101/hub")));
    conduit.Route("Conduit.Sample.Inventory").To("inventory");
});
```

This creates a `SignalRTransport` in client mode. Instead of an `IHubContext`, it wraps a `HubConnection` configured with `WithAutomaticReconnect()`. The connection is lazy -- it auto-starts on the first dispatch call via `EnsureConnectedAsync`, so your service starts without waiting for the remote hub to be available.

The remote service needs `AddConduitSignalR()` and `MapHub<ConduitHub>` to accept inbound requests:

```csharp
// Inventory service (remote)
builder.Services.AddConduit();
builder.Services.AddConduitSignalR();

var app = builder.Build();
app.MapHub<ConduitHub>("/hub");
app.Run();
```

```csharp
// Order service (caller)
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("inventory", endpoint =>
        endpoint.UseSignalR(new Uri("https://localhost:5101/hub")));
    conduit.Route("Conduit.Sample.Inventory").To("inventory");
});
```

When the order service dispatches a command in the `Conduit.Sample.Inventory` namespace, the generated mediator finds the route, serializes the command into a `DispatchEnvelope`, and sends it through the client-mode `SignalRTransport` to the inventory service's hub.

## Multi-pod deployment

On a single pod (or single server), everything works without extra configuration. The server-mode `SignalRTransport` calls `Clients.All.SendAsync`, which reaches all connections on that server.

For multiple pods, unsolicited server-to-client events only reach clients connected to the pod that published the event. To broadcast across pods, add a SignalR backplane:

- **Azure SignalR Service** -- managed, no infrastructure to maintain
- **Redis backplane** -- `Microsoft.AspNetCore.SignalR.StackExchangeRedis`

```csharp
// Example: Redis backplane
builder.Services.AddSignalR()
    .AddStackExchangeRedis("localhost:6379");
```

This is a standard ASP.NET Core concern, not Conduit-specific. The backplane ensures `Clients.All.SendAsync` reaches every connected client regardless of which pod they are connected to. Conduit's `SignalRTransport` does not need any changes -- it calls the same `IHubContext` API either way.

Solicited events (command triggers event on the same pod) work without a backplane as long as the client that sent the command is connected to the pod that handles it, which is always the case for direct hub calls.

Next: [HTTP Transport](http-transport.md)
