# SignalR Transport

- [Overview](#overview)
- [Setup](#setup)
  - [Server mode](#server-mode)
  - [Client mode (service-to-service)](#client-mode-service-to-service)
- [Options and configuration keys](#options-and-configuration-keys)
- [Correlation ids](#correlation-ids)
- [Client properties](#client-properties)
- [Server identity on connect](#server-identity-on-connect)
- [Bidirectional communication](#bidirectional-communication)
- [Who receives an event](#who-receives-an-event)
- [Solicited events](#solicited-events)
- [Multi-pod deployment](#multi-pod-deployment)
- [Request/response](#requestresponse)
- [Failure handling](#failure-handling)
- [Wire format](#wire-format)
- [Limitations](#limitations)

## Overview

SignalR provides persistent, bidirectional connections. Browser clients dispatch commands and receive events on the same connection. Services send to each other's hubs with automatic reconnect; a service's [client-mode transport](#client-mode-service-to-service) sends only, it does not receive the hub's events.

The transport has two modes. In **server mode** a host maps `ConduitHub`, which receives dispatches from its clients, and the transport pushes the host's events to them. In **client mode** an endpoint routes commands and queries to another service's hub.

## Setup

### Server mode

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddConduit();
builder.Services.AddConduitSignalR();

var app = builder.Build();
app.MapHub<ConduitHub>("/conduit");
app.Run();
```

`AddConduitSignalR()` registers SignalR services, creates a `SignalRTransport` in server mode, and registers it as `IConduitTransport`. The transport holds an `IHubContext<ConduitHub>` and pushes events to connected clients: by default only the `[ClientVisible]` ones ([Who receives an event](#who-receives-an-event)).

`MapHub<ConduitHub>` maps the inbound hub. It exposes two methods: `Dispatch` for commands/queries and `Publish` for events.

### Client mode (service-to-service)

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

The remote service needs `AddConduitSignalR()` and `MapHub<ConduitHub>` to accept requests. It keeps the caller's correlation ids, so the conversation spans both services, only with `TrustCallerContext` ([Correlation ids](#correlation-ids)); leave it off on a hub browsers can reach:

```csharp
// Inventory service (remote, reached only by other services)
builder.Services.AddConduit();
builder.Services.AddConduitSignalR(options => options.TrustCallerContext = true);

var app = builder.Build();
app.MapHub<ConduitHub>("/conduit");
app.Run();
```

## Options and configuration keys

Each mode has its own options class.

**Server mode.** `AddConduitSignalR(configure)` configures `ConduitSignalROptions`, how the hub treats its callers:

| Property | Default | Description |
|---|---|---|
| `TrustCallerContext` | `false` | Whether the hub honors the correlation ids a caller passes in the `context` argument ([Correlation ids](#correlation-ids)). Set it only on a hub that other services reach and browsers do not. |

`AddConduitSignalR(serverInfo, configure)` also takes a `ConduitServerInfo` the hub sends each client as it connects ([Server identity on connect](#server-identity-on-connect)). Conduit binds neither from configuration: set both in code. Call `AddConduitSignalR` once per host: a second call registers the transport for broadcast again, and every event is pushed twice.

**Client mode.** `UseSignalR(hubUri, configure)` configures `SignalROptions`:

| Property | Default | Description |
|---|---|---|
| `HubUri` | *(required)* | The remote hub's URI, e.g. `https://inventory-service/conduit`. `UseSignalR` throws `InvalidOperationException` when neither the `hubUri` argument nor configuration supplies one. |
| `ConfigureConnection` | `null` | Configures the `HubConnection`'s HTTP options (`AccessTokenProvider`, `Headers`, a proxy, client certificates). Code only; configuration never binds it. |

`HubUri` binds from `Conduit:Endpoints:{endpointName}:HubUri`, so the URI can come from configuration instead of the `hubUri` argument:

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

## Server identity on connect

A host can have `ConduitHub` tell every client about itself the moment it connects:

~~~csharp
builder.Services.AddConduitSignalR(new ConduitServerInfo(buildId));
~~~

`OnConnectedAsync` then invokes the client method `ConduitServerInfo` (`ConduitHub.ServerInfoMethod`) with
`{ buildId }` on every connect and every reconnect. Conduit carries the value and never derives it: a host that
registers none sends nothing. The TypeScript transport surfaces it as `SignalRTransport.onServerInfo`
([TypeScript Client → Server identity](typescript-client.md#server-identity)).

**On connect, not broadcast on restart.** A restart broadcast reaches the clients connected to that pod, which at
startup is nobody. The clients that need to know are still on the old pod, and events do not replay. Every client that
needs the id necessarily connects to the new pod.

## Bidirectional communication

A single SignalR connection carries traffic in both directions.

**Client to server.** Commands and queries go through the hub's `Dispatch` method. Events go through `Publish`. Both delegate to `IWireMediator`. A refused event (the [type filter](../security.md#typefilterinterceptor), an unknown type, a payload the event type cannot read) fails the `Publish` invocation with a `HubException` carrying the refusal's message.

**Server to client.** When a handler calls `mediator.PublishAsync(event)`, the server-mode `SignalRTransport` pushes a `[ClientVisible]` event to all connected clients via `Clients.All.SendAsync`. An event without `[ClientVisible]` is not pushed to any client by default; register an [`IEventRecipientResolver`](#who-receives-an-event) to decide otherwise, or to send an event to some clients only.

```csharp
[ClientVisible]
public record OrderShippedEvent(Guid OrderId, string CustomerId) : IEvent;
```

No subscription step is needed. Clients receive the events pushed to them by listening for the event's full type name:

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

## Who receives an event

By default the hub pushes a `[ClientVisible]` event to every connection, signed in or not, and any other event to none. `[ClientVisible]` is also what gives an event a TypeScript type, so the events the [TypeScript client](typescript-client.md) knows about are the events it receives. [`[LocalOnly]`](../security.md#localonly) keeps an event in the process entirely: it reaches no transport, this one included.

To scope events (to the users an event concerns, to a tenant's group) register an `IEventRecipientResolver`. Once one is registered it decides for **every** event the host publishes that is not `[LocalOnly]`, `[ClientVisible]` or not, in place of the default:

```csharp
public sealed class TenantRecipients : IEventRecipientResolver
{
    public ValueTask<EventRecipients> ResolveAsync(
        string eventType, object @event, CancellationToken cancellationToken)
    {
        var recipients = @event switch
        {
            OrderShippedEvent shipped => EventRecipients.Users([shipped.CustomerId]),
            InvoiceIssuedEvent issued => EventRecipients.Groups([$"tenant:{issued.TenantId}"]),
            PriceListChangedEvent => EventRecipients.All,
            _ => EventRecipients.None,
        };
        return ValueTask.FromResult(recipients);
    }
}

builder.Services.AddSingleton<IEventRecipientResolver, TenantRecipients>();
```

| Answer | Sent with |
|---|---|
| `EventRecipients.All` | `Clients.All` |
| `EventRecipients.None` (also `default`) | nothing |
| `EventRecipients.Users(userIds)` | `Clients.Users(userIds)`: every connection of each user, as the host's `IUserIdProvider` names a connection's user (by default its `ClaimTypes.NameIdentifier` claim) |
| `EventRecipients.Groups(groupNames)` | `Clients.Groups(groupNames)`: every connection in each group |

The resolver's answer is the whole decision: a `[ClientVisible]` event it answers `None` for reaches nobody, and an event without the attribute it answers `All` for reaches everyone. End the switch with `None`, so an event added later is not pushed until you decide who sees it. An empty `Users` or `Groups` list pushes nothing.

- **The event** arrives as its own type, deserialized from the payload the transport was handed with `ConduitRuntime.JsonOptions`; as the raw `JsonElement` when the host's registry does not list the type.
- **Groups** are SignalR's: the host adds connections to them, for example from an `IHubFilter`'s `OnConnectedAsync` (`context.Hub.Groups.AddToGroupAsync(context.Context.ConnectionId, ...)`), which runs for `ConduitHub` too.
- **Lifetime.** Register the resolver as a singleton: the server-mode transport is one and resolves it once. One that needs scoped services (a `DbContext` to look up a tenant's members) creates a scope from `IServiceScopeFactory` per call.
- **When it runs.** Once per event: inline in `PublishAsync`, or, when the [outbox](../outbox.md) captured the event, later, on the outbox's delivery (its flush or its relay) after the publisher's request has ended. There it has no ambient request state: `MessageContext.Current`'s properties and an `IHttpContextAccessor` are not the publisher's. Decide from the event itself, so carry what the decision needs (a tenant id, a user id) on the event.
- **Failure.** An exception the resolver throws, or a payload that does not read as the event's type, skips that event's push: the transport logs it as `RecipientResolutionFailed` (EventId 100, Warning, with the exception) and pushes the event to nobody. The transport does not throw for it, so the publish, the other transports and the outbox's delivery of the event carry on.

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
3. The server-mode `SignalRTransport` pushes the event to all connected clients (`OrderPlacedEvent` is `[ClientVisible]`), or to those your [resolver](#who-receives-an-event) names.
4. Browser receives `OrderPlacedEvent` on the same connection.
5. The `Dispatch` call also returns the command result as a `DispatchResult`.
   A failed result's message and details are written in the reader's language on the way out when the host registers an `IDispatchCultureResolver` and an `IRefusalTextRenderer` ([A refusal in the reader's language](../error-handling.md#a-refusal-in-the-readers-language)).

The command response and the event arrive independently. The response is the return value of `Dispatch`. The event arrives as a push.

## Multi-pod deployment

On a single pod, server-mode `SignalRTransport` calls `Clients.All.SendAsync` (or `Clients.Users`/`Clients.Groups`, for a [resolver's](#who-receives-an-event) answer) and reaches the matching connections on that server.

For multiple pods, unsolicited events only reach clients on the pod that published the event. To broadcast across pods, add a SignalR backplane:

```csharp
// Redis backplane
builder.Services.AddSignalR()
    .AddStackExchangeRedis("localhost:6379");
```

This is standard ASP.NET Core configuration, not Conduit-specific. The backplane ensures `Clients.All.SendAsync` reaches every connected client regardless of pod, and a user's or group's connections likewise. Conduit's transport works the same either way.

Solicited events (command triggers event on the same pod) work without a backplane because the client is always connected to the pod handling its request.

## Request/response

**Server mode.** The hub's `Dispatch` runs the command or query through `IWireMediator` and returns its `DispatchResult`, the handler's result in `data` or its failure in `error`. The dispatch runs under the connection's `ConnectionAborted` token, so a client that disconnects cancels the dispatches it has in flight.

**Client mode.** A routed command or query invokes the remote hub's `Dispatch` and waits for its result. A failed result throws `RemoteDispatchException` on the caller, carrying the remote error's code and, for an unexpected failure, its support reference. The transport sets no response timeout of its own: the call waits as long as the connection lives, so bound it with the caller's cancellation token where that matters.

## Failure handling

- **A handler that throws during `Dispatch`** is answered in the result: a `ConduitException` as its refusal (code, message, status), anything else as `InternalError` with the fixed message `An unexpected error occurred.` and a support reference, which the server logs with the exception. Nothing is retried or dead-lettered: the caller has its answer.
- **A refused event** (the type filter, an unknown type, a payload its type cannot read) fails the `Publish` invocation with a `HubException` carrying the refusal's message. A listener that throws fails it too, with SignalR's own generic message (`An unexpected error occurred invoking 'Publish' on the server.`) unless the host enables SignalR's detailed errors.
- **Client mode** throws `RemoteDispatchException` for a failed result, a refused `[FireAndForget]` command included, and SignalR's own exception when the connection cannot carry the call.
- **A push that fails** is the publisher's to log: a resolver fault skips the push (`RecipientResolutionFailed`, above), and a transport that throws is logged as `TransportBroadcastFailed` while the publish carries on to the other transports.

## Wire format

The hub speaks SignalR's JSON hub protocol, with payloads serialized by Conduit's wire options (`ConduitRuntime.JsonOptions`: camelCase names, enums by member name):

| Hub method | Arguments | Returns |
|---|---|---|
| `Dispatch` | `type` (full type name), `payload` (the serialized message), `context` (`MessageId`, `ConversationId`, `CausationId`, or `null`), `headers` (client properties, or `null`) | `DispatchResult` |
| `Publish` | the same four | nothing |

A caller passes all four arguments, `null` for the ones it does not use: SignalR refuses an invocation whose argument count does not match. The server pushes an event by invoking the client method named after the event's full type name with the payload as its one argument, and sends `ConduitServerInfo` (`{ buildId }`) on connect when the host registered one. The [TypeScript client's wire format](typescript-client.md#wire-format) shows the same exchange from the browser.

## Limitations

- **No cross-pod push without a backplane.** An unsolicited event reaches only the clients connected to the pod that published it ([Multi-pod deployment](#multi-pod-deployment)).
- **Client mode is send-only.** It does not receive the remote hub's events, and a client-mode endpoint is not a broadcast target: `PublishAsync` does not send events to the remote hub.
- **Server mode does not send.** It cannot carry a request or replay a dead letter (`SendRequestAsync` and `RedeliverAsync` throw `NotSupportedException`); only the broker transports capture dead letters.
- **No delivery guarantee for pushes.** An event reaches the clients connected when it is pushed; nothing is kept for a client that is offline or reconnecting.
- **Client-mode reconnect gives up.** The `HubConnection` uses SignalR's default automatic reconnect (after 0, 2, 10 and 30 seconds, then it stops); the next send starts the connection again, and a send made during a reconnect fails.
- **A dropped connection cancels its dispatches.** The hub dispatches under the connection's `ConnectionAborted` token.

---

Previous: [ActiveMQ](activemq.md) | [Home](../../README.md) | Next: [HTTP](http.md)
