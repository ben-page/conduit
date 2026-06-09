# Configuration Reference

- [ConduitBuilder methods](#conduitbuilder-methods)
- [ConduitBuilder properties](#conduitbuilder-properties)
- [EndpointBuilder methods](#endpointbuilder-methods)
- [Attributes](#attributes)
- [MSBuild properties (TypeGen)](#msbuild-properties-typegen)
- [Diagnostics](#diagnostics)

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
| `AddEndpoint(name, configure)` | Register a named remote endpoint with a transport and routes | `conduit.AddEndpoint("orders", e => { e.UseHttp(httpClient); e.RouteNamespace<CreateOrder>(); })` |
| `AddPipeline(type)` | Register an open-generic `IPipelineBehavior<,>`. First registered = outermost. | `conduit.AddPipeline(typeof(LoggingBehavior<,>))` |
| `AddTypeFilter(configure)` | Restrict which types external clients may dispatch | `conduit.AddTypeFilter(f => f.AllowType<CreateOrder>())` |
| `AddDurability(configure)` | Configure durable messaging: set the shared database once, then nest `AddOutbox()` / `AddInbox()` / `AddDeadLetterQueue()`. See [Outbox](outbox.md), [Inbox](inbox.md), and [Dead Letter Queue](dead-letter-queue.md). | `conduit.AddDurability(d => { d.UseSqlite("Data Source=app.db"); d.AddOutbox(); })` |
| `AddAudit(configure)` | Capture dispatch lifecycle observations and emit `AuditBatch` events. See [Audit Trail](audit.md). | `conduit.AddAudit(a => a.ServiceName = "orders")` |
| `AddAuditListener(configure)` | Consume `AuditBatch` events (local or over a transport) and persist them. See [Audit Trail](audit.md). | `conduit.AddAuditListener(l => l.UseSqlite("Data Source=audit.db"))` |

## ConduitBuilder properties

Properties set directly on the builder instance:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.SystemName = "myapp";
    conduit.ApplicationName = "orders-service";
    conduit.TopologyMode = TopologyMode.Developer;
    conduit.TopologyPrefix = "myprefix";  // optional, defaults to Environment.UserName
});
```

| Property | Type | Default | Description |
|---|---|---|---|
| `SystemName` | `string` | `"conduit"` | Base name for all broker resources (topic/exchange, queues). Replaces per-transport `TopicName`/`ExchangeName`. |
| `ApplicationName` | `string?` | `null` | Application name used in queue naming. Defaults to `IHostEnvironment.ApplicationName` at transport startup if not set. |
| `TopologyMode` | `TopologyMode` | `Production` | Controls resource naming and durability. `Production` = durable, no prefix. `Developer` = prefixed, 4h auto-delete. `Test` = random prefix, 5min auto-delete. |
| `TopologyPrefix` | `string?` | auto-resolved | Prefix for broker resource names. Auto-resolves on first access: Developer = `Environment.UserName`, Test = random 13-char prefix, Production = null. Set explicitly to share topology across hosts. |
| `ProducerOnly` | `bool` | `false` | When true, only publishes messages -- skips inbound topology and consumers. Reply queues for request/response still created. |

Example `appsettings.Development.json` for developer mode:

```json
{
  "Conduit": {
    "TopologyMode": "Developer"
  }
}
```

## EndpointBuilder methods

Available inside the `AddEndpoint` callback:

| Method | Purpose | Example |
|---|---|---|
| `RouteNamespace<T>()` | Route all types in `T`'s namespace to this endpoint | `ep.RouteNamespace<CreateOrder>()` |
| `RouteType<T>()` | Route a single type to this endpoint | `ep.RouteType<CreateOrder>()` |
| `UseSignalR(uri)` | Connect via SignalR client mode | `ep.UseSignalR(new Uri("https://..."))` |
| `UseHttp(httpClient)` | Connect via HTTP POST to `/api/conduit/dispatch` | `ep.UseHttp(httpClient)` |
| `UseAzureServiceBus(conn)` | Connect via Azure Service Bus | `ep.UseAzureServiceBus(connectionString)` |
| `UseRabbitMQ(conn)` | Connect via RabbitMQ | `ep.UseRabbitMQ(connectionString)` |

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
