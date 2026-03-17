# Configuration Reference

## ConduitBuilder methods

Available inside the `AddConduit` callback:

```csharp
builder.Services.AddConduit(conduit =>
{
    // methods below
});
```

| Method | Purpose | Example |
|---|---|---|
| `AddEndpoint(name, configure)` | Register a named remote endpoint with a transport | `conduit.AddEndpoint("orders", e => e.UseHttp(httpClient))` |
| `Route(pattern)` | Route commands/queries by namespace prefix to an endpoint | `conduit.Route("Orders.Commands").To("orders")` |
| `Route<T>()` | Route using a type's namespace as the pattern | `conduit.Route<CreateOrder>().To("orders")` |
| `AddPipeline(type)` | Register an open-generic `IPipelineBehavior<,>`. First registered = outermost. | `conduit.AddPipeline(typeof(LoggingBehavior<,>))` |
| `AddTypeFilter(configure)` | Restrict which types external clients may dispatch | `conduit.AddTypeFilter(f => f.AllowType<CreateOrder>())` |

## Endpoint transports

Available inside the `AddEndpoint` callback:

| Method | Transport | Notes |
|---|---|---|
| `UseSignalR(uri)` | SignalR client mode | Connects to a remote `ConduitHub` |
| `UseHttp(httpClient)` | HTTP | POSTs to `/api/dispatch` on the remote service |
| `UseAzureServiceBus(conn)` | Azure Service Bus | Durable broker-based messaging |
| `UseRabbitMQ(conn)` | RabbitMQ | Durable broker-based messaging over AMQP |

## Attributes

| Attribute | Target | Purpose |
|---|---|---|
| `[LocalOnly]` | Event types | Prevent event from broadcasting to transports |
| `[ClientVisible]` | Any type | Include in TypeScript generation output |
| `[assembly: RemoteContracts("Ns")]` | Assembly | Suppress CONDUIT001 for types in namespace |

## MSBuild properties (TypeGen)

| Property | Default | Purpose |
|---|---|---|
| `ConduitTypeGenEnabled` | `false` | Set to `true` to enable TypeScript generation |
| `ConduitTypeGenOutput` | `ClientApp/generated` | Output directory for `.ts` files |
| `ConduitTransports` | `Http;SignalR` | Transports to include in `conduit.ts` |

## Diagnostics

| Code | Severity | Description | Resolution |
|---|---|---|---|
| CONDUIT001 | Warning | No handler found for a command or query | Add a handler or use `[assembly: RemoteContracts("Ns")]` |
| CONDUIT002 | Error | Multiple handlers for a command or query | Remove the duplicate handler |
| CONDUIT004 | Warning | Type has neither `[Authorize]` nor `[AllowAnonymous]` | Add one of the attributes |

---

Previous: [Security](security.md) | [Home](../README.md) | Next: [FAQ](faq.md)
