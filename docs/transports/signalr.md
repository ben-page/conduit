# SignalR Transport

- [Overview](#overview)
- [Server setup](#server-setup)
- [Correlation ids](#correlation-ids)
- [Client properties](#client-properties)
- [Bidirectional communication](#bidirectional-communication)
- [Solicited events](#solicited-events)
- [Client mode (service-to-service)](#client-mode-service-to-service)
- [Multi-pod deployment](#multi-pod-deployment)

## Overview

SignalR provides persistent, bidirectional connections. Browser clients dispatch commands and receive events on the same connection. Services send to each other's hubs with automatic reconnect; a service's [client-mode transport](#client-mode-service-to-service) sends only, it does not receive the hub's events.

## Server setup

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddConduit();
builder.Services.AddConduitSignalR();

var app = builder.Build();
app.MapHub<ConduitHub>("/conduit");
app.Run();
```

`AddConduitSignalR()` registers SignalR services, creates a `SignalRTransport` in server mode, and registers it as `IConduitTransport`. The transport holds an `IHubContext<ConduitHub>` and pushes events to connected clients.

`MapHub<ConduitHub>` maps the inbound hub. It exposes two methods: `Dispatch` for commands/queries and `Publish` for events.

## Correlation ids

`Dispatch` and `Publish` take an optional `context` argument carrying `MessageContext` ids (`MessageId`, `ConversationId`, `CausationId`). By default the hub ignores it and the server mints fresh ids for every invocation, so each one is the head of a new conversation. The browser client passes `null` anyway. These ids are not cosmetic: the [inbox](../inbox.md) deduplicates on the `MessageId` and the audit trail links records by the other two, so a caller that could set them could suppress another dispatch or graft its own onto someone else's conversation.

A hub that only other services reach (`SignalRTransport` in client mode, which sends its context whenever all three ids are plain tokens, and none otherwise) can opt in, so a conversation carries on across the hop:

```csharp
builder.Services.AddConduitSignalR(options => options.TrustCallerContext = true);
```

A trusting hub honors the context only when `MessageId` and `ConversationId` are both present and every supplied id is a plain token: 1 to 64 ASCII letters, digits and hyphens (`MessageContextIds.IsValid`). An absent `CausationId` defaults to the message id. Any other context is ignored as a whole and the server mints fresh ids; the invocation is answered as usual. The `headers` argument and your `IDispatchContextEnricher`s are unaffected: enrichers run on every invocation and win over a caller's header of the same key.

## Client properties

The `headers` argument of `Dispatch` and `Publish` seeds the invocation's `MessageContext.Properties` (the [TypeScript client](typescript-client.md#properties)'s `headers`); your `IDispatchContextEnricher`s run after. **Property names are case-insensitive**: `Properties["TenantCode"]` finds a client's `tenantcode`, as over HTTP.

**An enricher that owns a key must write it or remove it on every invocation, unauthenticated ones included.** An enricher's write wins over a client's header of the same key, but an invocation it leaves alone keeps whatever the client sent: an enricher that returns early without a signed-in user lets an anonymous caller dispatch with `UserId=victim`. With no identity, remove the keys you own:

```csharp
var sub = user?.FindFirst("sub")?.Value;
if (sub is null) { properties.Remove("UserId"); return; }
```

`ConduitSignalROptions` configures the hub (the inbound side). `SignalROptions` configures the outbound client-mode transport. `AddConduitSignalR(serverInfo, configure)` takes both a `ConduitServerInfo` and the options callback.

## Server identity on connect

A host can have `ConduitHub` tell every client about itself the moment it connects:

~~~csharp
builder.Services.AddConduitSignalR(new ConduitServerInfo(buildId));
~~~

`OnConnectedAsync` then invokes the client method `ConduitServerInfo` (`ConduitHub.ServerInfoMethod`) with
`{ buildId }` on every connect and every reconnect. Conduit carries the value and never derives it: a host that
registers none sends nothing. The TypeScript transport surfaces it as `SignalRTransport.onServerInfo`
(`docs/transports/typescript-client.md` → "Server identity").

**On connect, not broadcast on restart.** A restart broadcast reaches the clients connected to that pod, which at
startup is nobody. The clients that need to know are still on the old pod, and events do not replay. Every client that
needs the id necessarily connects to the new pod.

## Bidirectional communication

A single SignalR connection carries traffic in both directions.

**Client to server.** Commands and queries go through the hub's `Dispatch` method. Events go through `Publish`. Both delegate to `IWireMediator`. A refused event (the [type filter](../security.md#typefilterinterceptor), an unknown type) fails the `Publish` invocation with a `HubException` carrying the refusal's message.

**Server to client.** When a handler calls `mediator.PublishAsync(event)`, the server-mode `SignalRTransport` pushes the event to all connected clients via `Clients.All.SendAsync`.

No subscription step is needed. Clients receive events by listening for the event's full type name:

```typescript
// TypeScript (browser)
client.on('MyApp.Events.OrderShippedEvent', (event) => {
  console.log('Shipped:', event.orderId);
});
```

```csharp
// C# (your own SignalR HubConnection: Conduit's client-mode transport does not receive events)
hubConnection.On<JsonElement>("MyApp.Events.OrderShippedEvent", payload =>
{
    Console.WriteLine($"Shipped: {payload}");
});
```

## Solicited events

The most common pattern: send a command, receive the resulting event on the same connection.

```csharp
// Server handler publishes an event after processing
public class PlaceOrderHandler(IMediator mediator)
    : ICommandHandler<PlaceOrderCommand, Guid>
{
    public async Task<Guid> HandleAsync(
        PlaceOrderCommand command, CancellationToken cancellationToken)
    {
        var id = orderStore.Add(command);
        await mediator.PublishAsync(
            new OrderPlacedEvent(id, command.Total), cancellationToken);
        return id;
    }
}
```

```typescript
// Browser: subscribe to the event, then send the command
client.on('MyApp.Events.OrderPlacedEvent', (event) => {
  console.log('Order placed:', event.orderId);
});

const result = await client.execute(
  'MyApp.Commands.PlaceOrderCommand',
  { total: 59.99 }
);
```

The flow:

1. Browser sends `PlaceOrderCommand` via the hub's `Dispatch` method.
2. Server handles the command and calls `PublishAsync(new OrderPlacedEvent(...))`.
3. The server-mode `SignalRTransport` broadcasts the event to all connected clients.
4. Browser receives `OrderPlacedEvent` on the same connection.
5. The `Dispatch` call also returns the command result as a `DispatchResult`.
   A failed result's message and details are written in the reader's language on the way out when the host registers an `IDispatchCultureResolver` and an `IRefusalTextRenderer` ([A refusal in the reader's language](../error-handling.md#a-refusal-in-the-readers-language)).

The command response and the event arrive independently. The response is the return value of `Dispatch`. The event arrives as a push.

## Client mode (service-to-service)

For a C# service that sends commands to a remote SignalR hub, configure an endpoint with `UseSignalR`:

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

This creates a `SignalRTransport` in client mode. It wraps a `HubConnection` with automatic reconnect. The connection is lazy and auto-starts on the first dispatch call, so your service starts without waiting for the remote hub. If the connection closes for good (automatic reconnect gave up, or the hub closed it), the next send starts it again; a send made while an automatic reconnect is still in progress fails as SignalR fails it. The container owns the transport and its `HubConnection`, and disposes both with the host.

**Client mode is send-only.** It sends commands and queries through the hub's `Dispatch`, and a routed `[FireAndForget]` command through `Dispatch` too, its result discarded (a refusal still throws). It does not receive the events the remote hub pushes: a service that needs another service's events uses a broker transport. Receiving events in client mode is deferred.

A hub mapped behind `RequireAuthorization` needs a credential on the connection. `SignalROptions.ConfigureConnection` reaches the `HubConnection`'s HTTP options (access token provider, headers, proxy, client certificates):

```csharp
endpoint.UseSignalR(new Uri("https://inventory-service/conduit"), options =>
    options.ConfigureConnection = connection =>
        connection.AccessTokenProvider = () => tokenSource.GetTokenAsync());
```

`ConfigureConnection` is code only; configuration never binds it.

The hub URI can also come from configuration instead of the `hubUri` argument:

```json
{
  "Conduit": {
    "Endpoints": {
      "inventory": {
        "HubUri": "https://inventory-service/conduit"
      }
    }
  }
}
```

```csharp
builder.Services.AddConduit(builder.Configuration, conduit =>
{
    conduit.AddEndpoint("inventory", endpoint =>
    {
        endpoint.UseSignalR();
        endpoint.RouteNamespace<Inventory.Commands.ReserveStock>();
    });
});
```

Configuration binds first; an explicit `UseSignalR(hubUri)` argument or a `configure` callback
overrides it. See [Loading from IConfiguration](../configuration-reference.md#loading-from-iconfiguration)
for the full precedence rule.

The remote service needs `AddConduitSignalR()` and `MapHub<ConduitHub>` to accept requests. It keeps the caller's correlation ids, so the conversation spans both services, only with `TrustCallerContext` ([Correlation ids](#correlation-ids)); leave it off on a hub browsers can reach:

```csharp
// Inventory service (remote, reached only by other services)
builder.Services.AddConduit();
builder.Services.AddConduitSignalR(options => options.TrustCallerContext = true);

var app = builder.Build();
app.MapHub<ConduitHub>("/conduit");
app.Run();
```

## Multi-pod deployment

On a single pod, server-mode `SignalRTransport` calls `Clients.All.SendAsync` and reaches all connections on that server.

For multiple pods, unsolicited events only reach clients on the pod that published the event. To broadcast across pods, add a SignalR backplane:

```csharp
// Redis backplane
builder.Services.AddSignalR()
    .AddStackExchangeRedis("localhost:6379");
```

This is standard ASP.NET Core configuration, not Conduit-specific. The backplane ensures `Clients.All.SendAsync` reaches every connected client regardless of pod. Conduit's transport works the same either way.

Solicited events (command triggers event on the same pod) work without a backplane because the client is always connected to the pod handling its request.

---

Previous: [Transports](index.md) | [Home](../../README.md) | Next: [TypeScript Client](typescript-client.md)
