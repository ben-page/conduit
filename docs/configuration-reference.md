# Configuration Reference

- [ConduitBuilder methods](#conduitbuilder-methods)
- [ConduitBuilder properties](#conduitbuilder-properties)
- [EndpointBuilder methods](#endpointbuilder-methods)
- [Loading from IConfiguration](#loading-from-iconfiguration)
  - [Core scalars](#core-scalars)
  - [Endpoint sections](#endpoint-sections)
  - [Precedence](#precedence)
  - [Pool merging](#pool-merging)
  - [Unmatched endpoint sections](#unmatched-endpoint-sections)
  - [Per-transport keys](#per-transport-keys)
  - [What configuration does not cover](#what-configuration-does-not-cover)
  - [Upgrading an existing host](#upgrading-an-existing-host)
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
| `AddDispatchRetry<TPolicy>(configure?)` | Retry a failed top-level command or query that `TPolicy` (a singleton `IDispatchRetryPolicy`) covers and gives a reason for, each attempt in a fresh DI scope. `DispatchRetryOptions`: `MaxAttempts` (default 3), `Delays` (jittered ranges, default 10-100 ms then 50-300 ms; the last repeats), `TimeProvider` (the retry clock, default `TimeProvider.System`). Once per host; options validated in the call. See [Dispatch Retry](dispatch-retry.md). | `conduit.AddDispatchRetry<MyRetryPolicy>(o => o.MaxAttempts = 4)` |
| `AddDurability(configure)` | Configure durable messaging by nesting `AddOutbox()` / `AddInbox()` / `AddDeadLetterQueue()`. The persistence engine is set once on the `ConduitBuilder` (via `conduit.UseSqlite(...)` etc.) and shared with audit. See [Outbox](outbox.md), [Inbox](inbox.md), and [Dead Letter Queue](dead-letter-queue.md). | `conduit.UseSqlite("Data Source=app.db"); conduit.AddDurability(d => d.AddOutbox());` |
| `AddAudit(configure)` | Capture dispatch lifecycle observations and emit `AuditBatch` events. `MaxPayloadBytes` (default 64 KB, `0` = no cap, an enabled cap must be at least 1 KB) caps each record's request body in UTF-8 bytes when the record seals: a body over it is stored with its longest strings shortened and a `"$truncated": { "bytes", "cap" }` marker, or as the marker alone when that cannot fit. See [Audit Trail](audit.md#payload-size-cap). | `conduit.AddAudit(a => a.ServiceName = "orders")` |
| `AddAuditListener(configure?)` | Consume `AuditBatch` events (local or over a transport) and persist them. Set the persistence engine separately via `conduit.UseSqlite(...)` (shared with durability). See [Audit Trail](audit.md). | `conduit.UseSqlite("Data Source=audit.db"); conduit.AddAuditListener();` |

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

## Loading from IConfiguration

Everything above can also be supplied from `IConfiguration`, so operational values (connection
strings, regions, timeouts) live in `appsettings.json` instead of being compiled in. Pass the
configuration as a second argument to `AddConduit`:

```csharp
builder.Services.AddConduit(builder.Configuration, conduit =>
{
    conduit.AddEndpoint("orders", endpoint =>
    {
        endpoint.UseRabbitMQ();
        endpoint.RouteNamespace<CreateOrderCommand>();
    });

    conduit.AddEndpoint("inventory", endpoint =>
    {
        endpoint.UseSignalR();
        endpoint.RouteNamespace<ReserveInventoryCommand>();
    });
});
```

This overload binds the `Conduit` section onto `ConduitOptions` and hands the section to every
`AddEndpoint` call, so each endpoint's `Use*` extension can bind its own transport options from
`Conduit:Endpoints:{endpointName}`. Endpoints, routes, pipelines, and type filters stay
code-declared — only core options and transport options come from configuration.

`AddConduit(IConfiguration, Action<ConduitBuilder>?)` is a second overload of the same
source-generated `AddConduit` — pass the plain `IConfiguration` (or an `IConfigurationSection` if
you already have the `Conduit` section) and an optional callback, same as the code-only overload.

**The configuration shape mirrors the fluent builder API, not the runtime object graph.** A key's
name and position is exactly the name of the property that fluent call sets, nested the same way
the property is nested. `Conduit:Endpoints:orders` corresponds to `AddEndpoint("orders", …)`;
`Conduit:Endpoints:events:VisibilityTimeoutSeconds` corresponds to
`AmazonSqsOptions.VisibilityTimeoutSeconds`. If you know the C# property name, you know the key — there is no separate config
vocabulary to learn. (`AmazonSqsOptions.Credentials` is the one exception to this — see
[Per-transport keys](#per-transport-keys) below, where it is mapped by hand rather than bound
generically.)

### Core scalars

The `Conduit` section binds five scalars onto `ConduitOptions`, the same ones settable as
`ConduitBuilder` properties above:

| Key | Type | Notes |
|---|---|---|
| `Conduit:SystemName` | `string` | |
| `Conduit:ApplicationName` | `string?` | |
| `Conduit:TopologyMode` | `TopologyMode` | Enum member name: `Production`, `Developer`, or `Test` |
| `Conduit:TopologyPrefix` | `string?` | Setting this key resolves the lazy prefix immediately, same as an explicit code assignment |
| `Conduit:ProducerOnly` | `bool` | |

A key absent from configuration leaves the property's existing default untouched — it is never
bound to `null`/`false`/etc.

`ConduitOptions.Endpoints` (`Dictionary<string, ConduitEndpoint>`), `Routes`
(`List<ConduitRoute>`), and `PipelineTypes` (`List<Type>`) are never bound from configuration:
`Conduit:Endpoints` holds transport options, not `ConduitEndpoint` instances, and the other two
have no configuration-friendly shape (required members, `Type` values).

### Endpoint sections

Each endpoint's transport options bind from `Conduit:Endpoints:{endpointName}`, where
`{endpointName}` is the exact string passed to `AddEndpoint`. Worked example combining every
transport:

```jsonc
{
  "Conduit": {
    "SystemName": "acme",
    "ApplicationName": "orders-api",
    "TopologyMode": "Developer",
    "TopologyPrefix": "ben",
    "ProducerOnly": false,

    "Endpoints": {
      "orders": {
        "ConnectionString": "amqp://guest:guest@rmq:5672/",
        "DefaultConcurrency": 20,
        "ResponseTimeout": "00:00:30",
        "Pools": [
          { "Name": "bulk", "Concurrency": 50, "MessageTypes": [ "Orders.Commands.Import" ] }
        ]
      },
      "events": {
        "Region": "eu-west-1",
        "Credentials": { "AccessKey": "AKIA…", "SecretKey": "…" },
        "VisibilityTimeoutSeconds": 120
      },
      "inventory": { "HubUri": "https://inventory/conduit" },
      "billing":   { "BaseAddress": "https://billing", "BasePath": "/api/conduit" }
    }
  }
}
```

Here `orders` is a RabbitMQ (or any other broker) endpoint, `events` is Amazon SQS, `inventory` is
SignalR, and `billing` is HTTP — see [Per-transport keys](#per-transport-keys)
below for the full key set each transport reads.

### Precedence

**Configuration binds first, code runs second — the `configure` callback is always the final
word.** Every `Use*` extension follows the same order: bind from `EndpointBuilder.Configuration`,
apply the extension's explicit argument if one was passed and is non-null (e.g. `UseRabbitMQ("amqp://…")`),
then invoke the `configure` callback if one was given. An explicit `UseRabbitMQ("amqp://prod")`
can never be silently redirected by a stray `appsettings.json` entry.

### Pool merging

Named receiver pools (`options.AddPool(name, configure)`, shared by all four broker transports)
declare in both places without conflicting:

- **`AddPool` is get-or-create.** If configuration already produced a pool with that name, the
  code callback configures the *same* pool instead of adding a duplicate.
- **Name matching is case-insensitive.** A pool named `"Bulk"` in configuration and
  `AddPool("bulk", …)` in code merge into one pool, not two. Because configuration binds first and
  creates the pool instance, **the configured spelling of `Name` is the one that survives** into
  broker resource naming — the code callback's casing of the name argument is only used to look the
  pool up, never to rename it.
- **`ReceiverPoolOptions.Handle` de-duplicates.** `Handle(string)`, `Handle<TMessage>()`, and
  `Handle(Type)` all resolve to the same `FullTypeName` and skip a type already present. A pool
  assembled from both sources ends up with the **union** of its configured `MessageTypes` and its
  code-declared `Handle(...)` calls.

```jsonc
{ "Name": "bulk", "Concurrency": 50, "MessageTypes": [ "Orders.Commands.Import" ] }
```
```csharp
options.AddPool("bulk", pool => pool.Handle<ReindexCommand>());
// → one pool "bulk", Concurrency 50, handling [Orders.Commands.Import, Orders.Commands.ReindexCommand]
```

Everything else in a pool section binds the same as any other options property: `Concurrency`
follows the precedence rule above (a code-set value on an existing pool overrides the configured
one), and `Name` must be present and unique within `Pools` or `ConduitConfigurationBinder` throws,
naming the offending configuration path.

### Unmatched endpoint sections

A `Conduit:Endpoints:{name}` section whose name matches no `AddEndpoint` call in this process is
**ignored silently** — nothing ever reads it, because `EndpointBuilder.Configuration` is only
materialized for endpoints you actually declare. This is deliberate: one shared `appsettings.json`
can describe every endpoint across a fleet of hosts, and each host's `AddEndpoint` calls pick out
just the subset it declares. Failing fast on an unrecognized section would break that pattern, at
the cost of not catching a typo'd endpoint name — the tradeoff this design makes on purpose.

### Per-transport keys

Every key below is a settable property on that transport's options type, bound from
`Conduit:Endpoints:{endpointName}`. `EndpointName` itself is set internally by the `Use*` extension
and is not configurable. `DefaultConcurrency` and `Pools` are common to every broker transport
(RabbitMQ, ActiveMQ, Azure Service Bus, Amazon SQS) — both are inherited from
`BrokerTransportOptions` and listed once per table below for completeness.

**RabbitMQ** (`endpoint.UseRabbitMQ()`)

| Key | Type | Notes |
|---|---|---|
| `ConnectionString` | `string` | Required (or pass it to `UseRabbitMQ(connectionString)`) |
| `DefaultConcurrency` | `int` | Default `10` |
| `ResponseTimeout` | `TimeSpan` | Default `00:01:00` |
| `Pools` | array | See [Pool merging](#pool-merging) |

**ActiveMQ** (`endpoint.UseActiveMQ()`)

| Key | Type | Notes |
|---|---|---|
| `ConnectionString` | `string` | Required (or pass it to `UseActiveMQ(connectionString)`) |
| `Username` | `string` | Optional, SASL PLAIN |
| `Password` | `string` | Optional, SASL PLAIN |
| `DefaultConcurrency` | `int` | Default `10` |
| `ResponseTimeout` | `TimeSpan` | Default `00:01:00` |
| `Pools` | array | See [Pool merging](#pool-merging) |

**Azure Service Bus** (`endpoint.UseAzureServiceBus()`)

| Key | Type | Notes |
|---|---|---|
| `ConnectionString` | `string` | Required (or pass it to `UseAzureServiceBus(connectionString)`) |
| `AdminConnectionString` | `string` | Optional; defaults to `ConnectionString`. Needed for the ASB Emulator (separate AMQP/HTTP ports) |
| `DefaultConcurrency` | `int` | Default `10` |
| `ResponseTimeout` | `TimeSpan` | Default `00:01:00` |
| `Pools` | array | See [Pool merging](#pool-merging) |

**Amazon SQS** (`endpoint.UseAmazonSqs()`)

| Key | Type | Notes |
|---|---|---|
| `Region` | `string` | AWS system name, e.g. `"eu-west-1"` — mapped via `RegionEndpoint.GetBySystemName` |
| `Credentials:AccessKey` | `string` | Nested under `Credentials`, never a flat `AccessKey` |
| `Credentials:SecretKey` | `string` | |
| `Credentials:SessionToken` | `string` | Optional; presence selects `SessionAWSCredentials` over `BasicAWSCredentials` |
| `ServiceUri` | `Uri` | For LocalStack/local development |
| `DefaultConcurrency` | `int` | Default `10` |
| `ResponseTimeout` | `TimeSpan` | Default `00:01:00`; must be `<= VisibilityTimeoutSeconds` |
| `VisibilityTimeoutSeconds` | `int` | Default `60` |
| `RawMessageDelivery` | `bool` | Default `true` |
| `Pools` | array | See [Pool merging](#pool-merging) |

`Region` and `Credentials` are excluded from the generic bind and mapped by hand: the binder
resolves `RegionEndpoint` to `null` silently and throws outright on the abstract `AWSCredentials`.
`RegionEndpoint.GetBySystemName` accepts any well-formed name without checking it against a known
list, so a typo in `Region` surfaces later as a connection failure rather than at startup.
Leaving `Credentials` unset (or supplying no `AccessKey`/`SecretKey` pair) falls back to the
default AWS credential chain — this is the common case, not an error state.

**SignalR** (`endpoint.UseSignalR()`)

| Key | Type | Notes |
|---|---|---|
| `HubUri` | `Uri` | Required (or pass it to `UseSignalR(hubUri)`) |

**HTTP** (`endpoint.UseHttp()`)

| Key | Type | Notes |
|---|---|---|
| `BaseAddress` | `Uri` | Required. Registers a named `HttpClient` via `AddHttpClient` |
| `BasePath` | `string` | Default `/api/conduit` |

The caller-owned `UseHttp(httpClient, basePath?)` overload never reads `BaseAddress` from
configuration — it exists for when you manage the `HttpClient` lifetime yourself (e.g. via
`IHttpClientFactory` elsewhere), and the `HttpClient` you pass in already has a `BaseAddress`. It
does still read the endpoint's configured `BasePath` when the `basePath` argument is omitted, so
`Conduit:Endpoints:{name}:BasePath` applies to either `UseHttp` overload.

### What configuration does not cover

Configuration supplies an endpoint's *values*. It never declares its *existence*, and it does not
reach the rest of the library:

- **Endpoints and routes stay in code.** `AddEndpoint`, `RouteNamespace<T>()`, and `RouteType<T>()`
  are compile-time declarations. Declaring them in configuration would need a transport-name
  registry across all six transport packages, so a string like `"RabbitMQ"` could resolve to a
  `Use*` call, plus a string-based route API to replace the generic ones — neither exists, and
  routing by string would give up the compile-time type checking the generic overloads buy.
- **Pipelines and type filters stay in code.** `AddPipeline(typeof(...))` and `AddTypeFilter(...)`
  take `Type` values; a configuration binder cannot produce one.
- **Durability, audit, resilience, and persistence are not configurable.** `AddDurability(...)`,
  `AddAudit(...)`, `AddResilience(...)`, and the engine calls (`UseSqlite(...)`, `UsePostgres(...)`,
  …) read nothing from the `Conduit` section. Their connection strings come from wherever your code
  reads them.
- **No reload-on-change.** Options bind once during `AddConduit` and are registered as plain
  singletons, not `IOptionsMonitor<T>`. Editing `appsettings.json` on a running host changes
  nothing until it restarts.

### Upgrading an existing host

Every existing code-only registration still compiles unchanged — the connection-string and hub-URI
arguments became optional rather than moving. Two consequences are worth knowing before you upgrade
the packages:

- **Recompile downstream assemblies.** `RabbitMQOptions.ConnectionString`,
  `ActiveMQOptions.ConnectionString`, and `AzureServiceBusOptions.ConnectionString` relaxed from `required init` to `{ get; set; }` so configuration
  can supply them. That is source-compatible — object initializers still work — but *binary*
  breaking: dropping `init` removes the setter's `IsExternalInit` modreq, so an assembly compiled
  against the previous package version must be rebuilt rather than swapped in. `UseSignalR` also
  gained parameters, so it is binary breaking in the same way.
- **`JobSchedule.FromInterval` gained a trailing optional `runsAtStart` parameter** for
  `ScheduleBuilder.RunAtStart()`. Callers still compile unchanged, but the method's signature changed,
  so an assembly compiled against the previous package version must be rebuilt rather than swapped in
  (binary breaking, as above).
- **A missing required value now throws `InvalidOperationException`.**
  Where `UseRabbitMQ(null)` or `UseSignalR(null)` used to fail immediately in a guard clause with
  an `ArgumentNullException`/`ArgumentException`, the
  value is now validated after configuration and code have both been applied, and the message names
  both ways to supply it: *"RabbitMQ transport for endpoint 'orders' has no connection string. Set
  'Conduit:Endpoints:orders:ConnectionString' in configuration or pass it to UseRabbitMQ()."*
  It is still thrown from the `AddConduit` call, so a misconfigured host still fails at startup.

## Attributes

| Attribute | Target | Purpose |
|---|---|---|
| `[LocalOnly]` | Event types | Prevent event from broadcasting to transports; inherited by a derived event |
| `[ClientVisible]` | Any type | Include in TypeScript generation output |
| `[DoNotAudit]` | Type, property, record parameter | Type: no payload captured. Member: left out of the captured body. A raw-JSON member (`JsonElement`, `JsonNode`, `object`, extension data) is otherwise captured verbatim, so mark it, type it with `[AuditAs]`, or suppress the type's payload |
| `[AuditRedacted]` | Property, record parameter | Audit payload holds `"•••"` for a present value (null/`""` as themselves), never the value; not combinable with `[DoNotAudit]` (CONDUIT005) |
| `[AuditAs(nameof(Type))]` | Property, record parameter | On a `JsonElement`/`JsonElement?` member holding a serialized message named by the sibling `string` property: the audit payload holds that message redacted by its own marks; `"•••"` when the name is not a registered message type or the JSON does not read as it; left out when that type is `[DoNotAudit]` or suppressed by `AddAudit` (`Suppress<T>()`/`SuppressPayload<T>()`) (CONDUIT006 when misapplied) |
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
| CONDUIT005 | Error | A message member is marked both [DoNotAudit] and [AuditRedacted] | Keep one |
| CONDUIT006 | Error | [AuditAs] on a member of a message, or of a type a message carries (in the compilation or a referenced assembly that references Conduit.Contracts), that is not JsonElement, or naming no sibling string property | Put it on a `JsonElement` member and name a `string` property |
| CONDUIT007 | Error | A message type is generic, or nested in a generic type | Declare one non-generic type per message |
| CONDUIT008 | Error | Two assemblies declare a message with the same full type name | Rename or move one of them |

---

Previous: [Security](security.md) | [Home](../README.md) | Next: [FAQ](faq.md)
