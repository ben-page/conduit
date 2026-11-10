# Configuration Reference

- [AddConduit](#addconduit)
- [ConduitBuilder properties](#conduitbuilder-properties)
- [ConduitBuilder methods](#conduitbuilder-methods)
  - [AddDispatchRetry options](#adddispatchretry-options)
- [Feature packages](#feature-packages)
  - [Persistence engines](#persistence-engines)
  - [Durability](#durability)
  - [Audit](#audit)
  - [Jobs](#jobs)
  - [Resilience](#resilience)
  - [The clock](#the-clock)
- [ConduitEndpointBuilder](#conduitendpointbuilder)
  - [Routes](#routes)
  - [Transports](#transports)
  - [Options every broker transport shares](#options-every-broker-transport-shares)
- [Registrations outside the builder](#registrations-outside-the-builder)
- [Topology modes](#topology-modes)
- [Loading from IConfiguration](#loading-from-iconfiguration)
  - [Core scalars](#core-scalars)
  - [Endpoint sections](#endpoint-sections)
  - [Precedence](#precedence)
  - [Pool merging](#pool-merging)
  - [Unmatched endpoint sections](#unmatched-endpoint-sections)
  - [Per-transport keys](#per-transport-keys)
  - [What configuration does not cover](#what-configuration-does-not-cover)
- [Attributes](#attributes)
- [MSBuild properties (TypeGen)](#msbuild-properties-typegen)
- [Diagnostics](#diagnostics)

## AddConduit

The source generator writes `AddConduit` into the host project, in the `Conduit.Generated` namespace. It registers
every discovered handler and listener, the generated mediator and Conduit's core services, and runs the callback.

| Overload | Purpose |
|---|---|
| `services.AddConduit(Action<ConduitBuilder>? configure = null)` | Code-only configuration. |
| `services.AddConduit(IConfiguration configuration, Action<ConduitBuilder>? configure = null)` | Binds the `Conduit` section first, then runs the callback, so code wins ([Loading from IConfiguration](#loading-from-iconfiguration)). Pass the application's configuration, or the `Conduit` section itself. |

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.SystemName = "acme";
    conduit.ApplicationName = "orders-api";
    conduit.TopologyMode = TopologyMode.Developer;
    conduit.TopologyPrefix = "alice";  // optional: Developer mode defaults to Environment.UserName

    conduit.AddEndpoint("orders", endpoint =>
    {
        endpoint.UseRabbitMQ("amqp://guest:guest@localhost:5672/");
        endpoint.RouteNamespace<CreateOrderCommand>();
    });
});
```

## ConduitBuilder properties

| Property | Type | Default | Description |
|---|---|---|---|
| `SystemName` | `string` | `"conduit"` | The base name of every broker resource (topic, exchange, queues). |
| `ApplicationName` | `string?` | `null` | The application's name in queue names. When unset, a transport uses `IHostEnvironment.ApplicationName` as it starts. |
| `TopologyMode` | `TopologyMode` | `Production` | Resource naming and lifetime: `Production`, `Developer` or `Test` ([Topology modes](#topology-modes)). |
| `TopologyPrefix` | `string?` | resolved on first read | The prefix of every broker resource name. Unset, it resolves to `Environment.UserName` in Developer mode, `test-` and 8 random hex characters in Test mode, and `null` in Production. Set it to share one prefix across hosts; a prefix set explicitly applies in Production too. Setting `null` returns it to the default. |
| `ProducerOnly` | `bool` | `false` | Publish only: a broker transport creates no inbound topology and starts no consumers. Reply queues for request/response are still created. |
| `Services` | `IServiceCollection` | | The service collection, for a feature's extension method. |
| `Options` | `ConduitOptions` | | What the builder fills: the five settings above, and the `Endpoints`, `Routes` and `PipelineTypes` the methods below add to. |
| `Configuration` | `IConfiguration?` | `null` | The `Conduit` section, once `UseConfiguration` has run. |

## ConduitBuilder methods

| Method | Purpose |
|---|---|
| `AddEndpoint(string name, Action<ConduitEndpointBuilder> configure)` | Declares a named remote endpoint; the callback picks its transport and routes ([ConduitEndpointBuilder](#conduitendpointbuilder)). A blank or repeated name throws `ArgumentException`. |
| `AddPipeline(Type behaviorType)` | Adds an open generic `IPipelineBehavior<,>` with two type parameters, such as `typeof(LoggingBehavior<,>)`; any other type throws `ArgumentException`. First registered is outermost. See [Pipeline Behaviors](pipeline-behaviors.md). |
| `AddTypeFilter(Action<TypeFilterBuilder> configure)` | Restricts the message types an inbound wire edge accepts, commands, queries and events, to those listed with `TypeFilterBuilder.AllowType(string fullTypeName)`. Repeated calls add to the list. See [Security](security.md#typefilterinterceptor). |
| `AddDispatchRetry<TPolicy>(Action<DispatchRetryOptions>? configure = null)` | Retries a failed top-level command or query that `TPolicy` (a singleton `IDispatchRetryPolicy`) covers and gives a reason for, each attempt in a fresh DI scope ([options below](#adddispatchretry-options)). A second call throws `InvalidOperationException`. See [Dispatch Retry](dispatch-retry.md). |
| `UseConfiguration(IConfiguration configuration)` | Binds the core settings and keeps the section for the endpoints. `AddConduit(IConfiguration, …)` calls it first; called by hand, it must come before every `AddEndpoint`, which reads the section when it runs. |

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddPipeline(typeof(LoggingBehavior<,>));
    conduit.AddTypeFilter(filter => filter.AllowType("MyApp.Commands.CreateOrderCommand"));
    conduit.AddDispatchRetry<MyRetryPolicy>(options => options.MaxAttempts = 4);
});
```

### AddDispatchRetry options

`DispatchRetryOptions`, validated in the call (`InvalidOperationException` names the option):

| Option | Default | Rule |
|---|---|---|
| `MaxAttempts` | `3` | At least 1; the first attempt counts. |
| `Delays` | 10–100 ms, then 50–300 ms | The wait before each retry, drawn uniformly from a range; the last range repeats. Not empty when `MaxAttempts` is above 1; each `Min` at least zero and at most its `Max`. |
| `TimeProvider` | `TimeProvider.System` | The retry clock. Never the host's DI `TimeProvider`. |

## Feature packages

Each feature package adds an extension method to `ConduitBuilder`. Options are validated in the call, and a value out
of range fails the host at startup.

### Persistence engines

| Method | Package |
|---|---|
| `UseSqlite(string connectionString)` | `Conduit.NET.Persistence.Sqlite` |
| `UsePostgres(string connectionString)` | `Conduit.NET.Persistence.Postgres` |
| `UseSqlServer(string connectionString)` | `Conduit.NET.Persistence.SqlServer` |
| `UseMySql(string connectionString)` | `Conduit.NET.Persistence.MySql` |

One engine serves every durable feature: the outbox, inbox and dead-letter queue, the audit listener, and jobs. Call it
before `AddJobs`. The outbox also needs a transaction-aware connection factory of your own
([Persistence](persistence.md#the-outbox-needs-a-transaction-aware-factory)).

### Durability

`AddDurability(Action<DurabilityBuilder> configure)` (`Conduit.NET.Durability`) takes three calls, each
`Add*(Action<options>? configure = null)`: `AddOutbox`, `AddInbox` and `AddDeadLetterQueue`. A value out of range
throws `OptionsValidationException`, naming each failure. A second call to any of them, from this builder or another
`AddDurability`, configures the same feature and registers nothing twice.

```csharp
conduit.UseSqlite("Data Source=app.db");
conduit.AddDurability(durability =>
{
    durability.AddOutbox(outbox => outbox.MaxRetries = 20);
    durability.AddInbox();
    durability.AddDeadLetterQueue();
});
```

`OutboxOptions` ([Outbox](outbox.md#relay-configuration)):

| Option | Default | Rule |
|---|---|---|
| `RelayInterval` | 5 s | How often the relay polls. Positive, at most one day. |
| `GracePeriod` | 10 s | The relay ignores entries newer than this. Not negative, at most one day. |
| `BatchSize` | `100` | Entries claimed per sweep. Positive. |
| `MaxRetries` | `10` | Attempts before an entry is marked failed, the first included. At least 2. |
| `InitialBackoff` | 5 s | The wait after the first failed attempt, doubled after each one after it. Not negative. |
| `MaxBackoff` | 5 min | The longest wait between attempts. At least `InitialBackoff`, at most one day. |
| `ClaimTimeout` | 5 min | After this a claimed entry counts as abandoned and another relay may take it ([claim timeout](outbox.md#claim-timeout)). Positive, at most one day. |
| `CompletedRetention` | 7 days | Sent entries are deleted this long after they were sent. `TimeSpan.Zero` keeps them. Not negative, at most 3650 days. |
| `FailedRetention` | `null` | Failed entries are deleted this long after their last attempt; `null` follows `CompletedRetention`, `TimeSpan.Zero` keeps them. Not negative, at most 3650 days. |
| `CleanupInterval` | 1 hour | How often the retention cleanup runs. Positive, at most one day. |
| `AutoCreateTable` | `true` | Creates the outbox table at startup. |
| `CaptureOnly` | `false` | Capture on the caller's transaction, never deliver: another process's relay owns delivery ([capture-only](outbox.md#capture-only)). |
| `PersistedProperties` | empty | The `MessageContext.Properties` keys an entry carries to its delivery, each with its type: `string`, `int`, `long`, `bool` or `Guid` ([carrying dispatch properties](outbox.md#carrying-dispatch-properties)). |

`InboxOptions` ([Inbox](inbox.md#configuration)):

| Option | Default | Rule |
|---|---|---|
| `RetentionPeriod` | 30 min | How long a processed message id is kept. Positive, at most 3650 days. |
| `CleanupInterval` | 5 min | How often expired ids are deleted. Positive, at most one day. |

`DeadLetterQueueOptions` ([Dead-Letter Queue](dead-letter-queue.md#setup)):

| Option | Default | Rule |
|---|---|---|
| `AutoCreateTable` | `true` | Creates the dead-letter table at startup. |

### Audit

`Conduit.NET.Audit` adds two methods. A value out of range throws `OptionsValidationException`, naming each failure,
and the same rules run on `IOptions<AuditOptions>` as the host starts ([validated options](audit.md#validated-options)).

`AddAudit(Action<AuditBuilder>? configure = null)` captures each dispatch and publishes `AuditBatch` events
([capture tuning](audit.md#capture-tuning)):

| Member | Default | Rule |
|---|---|---|
| `ServiceName` | the host's application name | The service named on each batch; `"unknown"` when the host has no name either. |
| `BatchSize` | `100` | Records per published batch. Positive. |
| `ChannelCapacity` | `10000` | Records buffered before `BackpressureMode` applies. Positive. |
| `BackpressureMode` | `DropOldest` | What a full buffer does (`BoundedChannelFullMode`). |
| `StaleGroupTimeout` | 5 min | A dispatch silent this long gets a provisional record, then its real one when it ends. Positive, at most one day. |
| `ShutdownGrace` | 5 s | How long the publisher drains queued records when the host stops. Not negative, at most one day. |
| `MaxPayloadBytes` | 64 KB | The largest request body a record keeps, in UTF-8 bytes; a longer one is shortened and marked `$truncated` ([payload cap](audit.md#payload-size-cap)). 0 or less means no cap; an enabled cap is at least 1 KB. |
| `Suppress<T>()` | | Records nothing for `T`. |
| `SuppressPayload<T>()` | | Records `T`'s dispatches without its body. |

`AddAuditListener(Action<AuditListenerBuilder>? configure = null)` persists `AuditBatch` events, from this process or
from other services over a transport, in the persistence engine ([retention](audit.md#retention)):

| Member | Default | Rule |
|---|---|---|
| `RetentionPeriod` | 90 days | How long a stored record is kept. Positive, at most 3650 days. |
| `PurgeInterval` | 24 hours | How often expired records are deleted. Positive, at most one day. |

### Jobs

`AddJobs(Action<JobsBuilder> configure)` (`Conduit.NET.Jobs`) declares schedules (`jobs.Schedule(name)`) and event
triggers (`jobs.On<TEvent>(name)`, the name optional) whose bodies are commands. With a persistence engine registered
before it, every schedule fires once across the cluster; without one, on every host. A value out of range throws
`InvalidOperationException` from the call. See [Background Jobs](jobs.md#configuration-reference).

| Member | Default | Rule |
|---|---|---|
| `HeartbeatInterval` | 30 s | How often a running host renews its claim. Positive. |
| `ClaimTimeout` | 2 min | How stale a claim must be before another host may take it. More than twice `HeartbeatInterval`, at most one day. |
| `MaxAttempts` | `3` | Attempts before a run is marked failed, the first included. Positive. |
| `RetryBackoff` | 1, 2, 4 min | The wait before each further attempt; the last repeats. At least one entry, each positive and at most one day. |
| `FailedRetention` | 7 days | How long a failed run is kept. Not negative, at most 3650 days. |
| `CompletedRetention` | 1 day | How long a completed event-triggered run is kept. Not negative, at most 3650 days. |
| `HistoryRetention` | 30 days | How long run history is kept. Not negative, at most 3650 days. |
| `CleanupInterval` | 1 hour | How often expired runs and history are deleted. Positive, at most one day. |
| `AutoCreateTable` | `true` | Creates the jobs tables at startup. |
| `ManageOnly` | `false` | Registers `IJobs` without the runner, for a host that must never run a job. |

### Resilience

`AddResilience(Action<ConduitResilienceBuilder>? configure = null)` (`Conduit.NET.Resilience`) adds a Polly
pipeline behavior as the outermost behavior, wherever the call stands among the `AddPipeline` calls. The builder names
the Polly pipelines it applies:

| Method | Applies to |
|---|---|
| `AddDefaultPipeline(Action<ResiliencePipelineBuilder> configure)` | Every dispatch no other pipeline covers. |
| `AddPipelineForType<T>(Action<ResiliencePipelineBuilder> configure)` | `T`; wins over every other pipeline. |
| `AddPipelineForNamespace(string namespacePrefix, Action<ResiliencePipelineBuilder> configure)` | Every type in the namespace and beneath it, on whole segments; the longest enclosing namespace wins. |

See [Pipeline Behaviors → Resilience](pipeline-behaviors.md#resilience-polly).

### The clock

`AddDurability`, `AddAudit`, `AddAuditListener` and `AddJobs` read "now" from the host's `TimeProvider` and register
`TimeProvider.System` when the host registers none; a host that registers its own keeps it
([Persistence → The clock](persistence.md#the-clock)). `AddDispatchRetry` waits on its own
`DispatchRetryOptions.TimeProvider`.

## ConduitEndpointBuilder

The `AddEndpoint` callback's argument. Its properties are for transport extensions: `EndpointName`, `Services`, and
`Configuration` (the endpoint's `Conduit:Endpoints:{name}` section, or `null`).

### Routes

| Method | Routes to this endpoint |
|---|---|
| `RouteNamespace<T>()` | Every command and query in `T`'s namespace and the namespaces beneath it, on whole segments: `Orders.Commands` never covers `Orders.CommandsArchive`. |
| `RouteType<T>()` | `T` alone: `CreateOrderLine` is not `CreateOrder`. |

Routes are tried in the order they were declared, across endpoints, and the first that matches wins, so declare a
`RouteType<T>()` before a `RouteNamespace` that encloses `T` if they name different endpoints. A command or query no
route matches runs locally. Events are not routed: they are broadcast ([Transports](transports/index.md#event-broadcasting)).

### Transports

One per endpoint. Each `Use*` method binds the endpoint's configuration section first, then applies its arguments,
then its callback ([Precedence](#precedence)). A required value neither supplies throws `InvalidOperationException`
from the call, naming the configuration key and the argument.

| Method | Package | Needs |
|---|---|---|
| `UseHttp(HttpClient httpClient, string? basePath = null)` | `Conduit.NET.Transport.Http` | A client with its `BaseAddress` set, which the transport holds for the host's life and never disposes. `basePath` defaults to the configured `BasePath`, then `/api/conduit`. |
| `UseHttp(Action<HttpOptions>? configure = null)` | `Conduit.NET.Transport.Http` | `BaseAddress`. Registers a named `HttpClient` for the endpoint; `BasePath` defaults to `/api/conduit`. |
| `UseSignalR(Uri? hubUri = null, Action<SignalROptions>? configure = null)` | `Conduit.NET.Transport.SignalR` | `HubUri`. `SignalROptions.ConfigureConnection` (an access token, headers) is code only. |
| `UseRabbitMQ(string? connectionString = null, Action<RabbitMQOptions>? configure = null)` | `Conduit.NET.Transport.RabbitMQ` | `ConnectionString` |
| `UseAzureServiceBus(string? connectionString = null, Action<AzureServiceBusOptions>? configure = null)` | `Conduit.NET.Transport.AzureServiceBus` | `ConnectionString` |
| `UseAmazonSqs(Action<AmazonSqsOptions>? configure = null)` | `Conduit.NET.Transport.AmazonSqs` | `Region`, or `ServiceUri` for LocalStack; with neither, the AWS SDK's default region and credential chain |
| `UseActiveMQ(string? connectionString = null, Action<ActiveMQOptions>? configure = null)` | `Conduit.NET.Transport.ActiveMQ` | `ConnectionString` |

Each transport's options and their defaults are in its guide: [SignalR](transports/signalr.md#options-and-configuration-keys),
[HTTP](transports/http.md#options-and-configuration-keys), [Azure Service Bus](transports/azure-service-bus.md#options-and-configuration-keys), [RabbitMQ](transports/rabbitmq.md#options-and-configuration-keys),
[Amazon SQS](transports/amazon-sqs.md#options-and-configuration-keys) and [ActiveMQ](transports/activemq.md#options-and-configuration-keys).

`UseTransport<TTransport>()`, `UseTransport<TTransport>(BrokerTransportOptions options)` and
`UseTransport(IConduitTransport transport)` register a transport by hand, for a transport package's own `Use*` method
or a test.

### Options every broker transport shares

`RabbitMQOptions`, `AzureServiceBusOptions`, `AmazonSqsOptions` and `ActiveMQOptions` derive from
`BrokerTransportOptions`:

| Member | Default | Description |
|---|---|---|
| `DefaultConcurrency` | `10` | How many messages the application's consumer handles at once: the prefetch count on RabbitMQ and ActiveMQ, the maximum concurrent calls on Azure Service Bus, the polling parallelism on Amazon SQS. |
| `ResponseTimeout` | 60 s | How long a request waits for its reply before it fails with a `TimeoutException`. Positive and finite, checked in the `Use*` call. |
| `AddPool(string name, Action<ReceiverPoolOptions> configure)`, `Pools` | empty | A named receiver pool: its own queue and consumer for the types it `Handle`s; `Pools` lists them. `ReceiverPoolOptions.Concurrency` defaults to `1`. The names `replies` and `dead-letter` are reserved ([Pool merging](#pool-merging)). |
| `EndpointName` | the endpoint's name | Set by the `Use*` call; read-only to you. |

## Registrations outside the builder

| Call | Package | Purpose |
|---|---|---|
| `services.AddConduitSignalR(Action<ConduitSignalROptions>? configure = null)` | `Conduit.NET.Transport.SignalR` | Registers SignalR, the hub's services and the server-mode transport that pushes events to browsers. `ConduitSignalROptions.TrustCallerContext` (default `false`) lets the hub honor its callers' correlation ids ([Security](security.md#correlation-ids)). |
| `services.AddConduitSignalR(ConduitServerInfo serverInfo, Action<ConduitSignalROptions>? configure = null)` | `Conduit.NET.Transport.SignalR` | The same, and the hub sends `serverInfo` to each client as it connects. |
| `app.MapHub<ConduitHub>(string pattern)` | ASP.NET Core | Maps the hub browsers and client-mode services connect to. |
| `app.MapConduitEndpoint(string basePath = "/api/conduit", bool trustCallerContext = false, bool readClientProperties = false)` | `Conduit.NET.Transport.Http` | Maps `{basePath}/dispatch` and `{basePath}/publish` and returns their route group, so a convention chained on it applies to both ([Security](security.md#what-a-caller-may-set)). |
| `app.MapConduitGet<T>(pattern)`, `MapConduitPost<T>`, `MapConduitPut<T>`, `MapConduitPatch<T>`, `MapConduitDelete<T>`, `MapConduit<T>(method, pattern)` | generated with the HTTP transport | RESTful endpoints, one type each ([HTTP](transports/http.md#restful-endpoints)). |
| `routeOrGroup.WithConduitDispatchContext()` | `Conduit.NET.Transport.Http` | Starts the inbound dispatch context (your `IDispatchContextEnricher`s) before a minimal-API route runs, so its in-process dispatches carry the same properties as the wire. |
| `services.ConfigureConduitRegistrations(Action<ConduitRegistrationOverrides> configure)` | `Conduit.NET` | After `AddConduit`: clears or replaces auto-registered handlers and listeners, and the broker topology that goes with them: for test doubles, for several applications simulated in one test assembly, or for a host that must not subscribe to types it does not handle. |

## Topology modes

`TopologyMode` names a broker's resources and decides how long they live. HTTP and SignalR create no broker
resources.

| Mode | Prefix by default | Lifetime |
|---|---|---|
| `Production` | none | Durable; nothing is deleted. |
| `Developer` | `Environment.UserName` | Ephemeral, where the broker can delete a resource itself. |
| `Test` | `test-` and 8 random hex characters | As Developer, sooner. |

| Transport | Developer | Test |
|---|---|---|
| RabbitMQ | Non-durable and auto-delete: a queue goes when its last consumer leaves, an exchange when its last binding goes | As Developer, and the dead-letter queue expires once unused for 5 minutes |
| Azure Service Bus | Each topic, queue and subscription is deleted after 4 hours idle (`AutoDeleteOnIdle`) | After 5 minutes idle |
| ActiveMQ | A queue is deleted 4 hours after its last consumer left; the two addresses stay | 5 minutes; the addresses stay |
| Amazon SQS | Nothing is deleted: SNS and SQS have no auto-delete | Nothing is deleted |

On Amazon SQS, `AmazonSqsTopologyReaper.ReapByPrefixAsync(options, prefix)` deletes the topics and queues whose names
start with a prefix, as Conduit's own tests do once per test project. See each transport's guide, and
[Transports → Ephemeral resource cleanup](transports/index.md#ephemeral-resource-cleanup).

## Loading from IConfiguration

The core settings and every endpoint's transport options can come from `IConfiguration`, so operational values
(connection strings, regions, timeouts) live in `appsettings.json` rather than in code. Pass the configuration to
`AddConduit`:

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

This overload binds the `Conduit` section onto `ConduitOptions` and hands each `AddEndpoint` its
`Conduit:Endpoints:{endpointName}` section, which the endpoint's `Use*` method binds onto its transport's options.
Endpoints, routes, pipelines and type filters stay in code.

**The keys follow the code.** A key's name is the name of the property the corresponding code sets, nested as the
property is: `Conduit:Endpoints:orders` is `AddEndpoint("orders", …)`, and
`Conduit:Endpoints:events:VisibilityTimeoutSeconds` is `AmazonSqsOptions.VisibilityTimeoutSeconds`. If you know the
property, you know the key. Amazon SQS's `Region` and `Credentials` are the exception, mapped by hand
([Per-transport keys](#per-transport-keys)).

### Core scalars

The `Conduit` section binds the five settings that are also `ConduitBuilder` properties:

| Key | Type | Notes |
|---|---|---|
| `Conduit:SystemName` | `string` | |
| `Conduit:ApplicationName` | `string?` | |
| `Conduit:TopologyMode` | `TopologyMode` | `Production`, `Developer` or `Test` |
| `Conduit:TopologyPrefix` | `string?` | Set, it is used as given, as if set in code |
| `Conduit:ProducerOnly` | `bool` | |

A key that is absent, or an empty string, leaves the setting as it was. `ConduitOptions.Endpoints`, `Routes` and
`PipelineTypes` are never bound: `Conduit:Endpoints` holds transport options, not endpoint declarations.

### Endpoint sections

Each endpoint's transport options bind from `Conduit:Endpoints:{endpointName}`, where `{endpointName}` is the exact
string passed to `AddEndpoint`:

```jsonc
{
  "Conduit": {
    "SystemName": "acme",
    "ApplicationName": "orders-api",
    "TopologyMode": "Developer",
    "TopologyPrefix": "alice",

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
        "VisibilityTimeoutSeconds": 120
      },
      "inventory": { "HubUri": "https://inventory/conduit" },
      "billing":   { "BaseAddress": "https://billing", "BasePath": "/api/conduit" }
    }
  }
}
```

Here `orders` is a RabbitMQ endpoint (any broker reads the same shared keys), `events` Amazon SQS, `inventory`
SignalR and `billing` HTTP.

### Precedence

**Configuration binds first, code runs second, so the `configure` callback always has the last word.** Every `Use*`
method binds its section, then applies its explicit argument if one was passed and is not null (for example
`UseRabbitMQ("amqp://…")`), then runs its callback. An explicit `UseRabbitMQ("amqp://prod")` is never redirected by a
stray `appsettings.json` entry. The core settings follow the same order: `AddConduit(IConfiguration, …)` binds them
before the callback runs.

### Pool merging

A named receiver pool can be declared in configuration, in code, or in both:

- **`AddPool` is get-or-create.** If configuration already produced a pool of that name, the code callback
  configures the same pool instead of adding a second.
- **Names match case-insensitively**, and the configured spelling is the one that survives into the broker's
  resource names: the code's spelling only looks the pool up.
- **`Handle` skips a type the pool already has.** `Handle(string)`, `Handle<TMessage>()` and `Handle(Type)` resolve
  to the same full type name, so a pool from both sources handles the union of its configured `MessageTypes` and its
  code's `Handle` calls.
- **`Concurrency` defaults to `1`** and follows the precedence rule: a value set in code wins.

```jsonc
{ "Name": "bulk", "Concurrency": 50, "MessageTypes": [ "Orders.Commands.Import" ] }
```
```csharp
endpoint.UseRabbitMQ(configure: options =>
    options.AddPool("bulk", pool => pool.Handle<ReindexCommand>()));
// one pool "bulk", Concurrency 50, handling Orders.Commands.Import and Orders.Commands.ReindexCommand
```

A configured pool needs a `Name`, unique within `Pools`, or the `Use*` call throws `InvalidOperationException` naming
the configuration path. Two names are reserved, in any case and however they are written before the name is
sanitized for the broker (`dead letter` is `dead-letter`): `replies`, the name of a transport's reply queue, and
`dead-letter`, held for a broker-side dead-letter queue. `AddPool` throws `ArgumentException` for either, and
configuration `InvalidOperationException`.

### Unmatched endpoint sections

A `Conduit:Endpoints:{name}` section that no `AddEndpoint` call in the process names is ignored: only a declared
endpoint reads its section. One shared `appsettings.json` can therefore describe every endpoint of a fleet, each host
picking out the ones it declares. The price is that a misspelled endpoint name is not reported.

### Per-transport keys

Each transport reads the keys named after its options' properties. The four broker transports also read the shared
keys: `DefaultConcurrency`, `ResponseTimeout` and `Pools` (each with `Name`, `Concurrency` and `MessageTypes`).
Each guide's *Options and configuration keys* section has the full table, with types and defaults.

| Transport | Keys under `Conduit:Endpoints:{name}` | Guide |
|---|---|---|
| RabbitMQ | `ConnectionString`, the shared keys | [RabbitMQ](transports/rabbitmq.md#options-and-configuration-keys) |
| ActiveMQ | `ConnectionString`, `Username`, `Password`, the shared keys | [ActiveMQ](transports/activemq.md#options-and-configuration-keys) |
| Azure Service Bus | `ConnectionString`, `AdminConnectionString`, the shared keys | [Azure Service Bus](transports/azure-service-bus.md#options-and-configuration-keys) |
| Amazon SQS | `Region`, `ServiceUri`, `Credentials:AccessKey`, `Credentials:SecretKey`, `Credentials:SessionToken`, `VisibilityTimeoutSeconds`, `MaxReceiveCount`, `RawMessageDelivery`, the shared keys | [Amazon SQS](transports/amazon-sqs.md#options-and-configuration-keys) |
| SignalR (client mode) | `HubUri` | [SignalR](transports/signalr.md#options-and-configuration-keys) |
| HTTP (client) | `BaseAddress`, `BasePath` | [HTTP](transports/http.md#options-and-configuration-keys) |

Amazon SQS maps two keys by hand, because the binder cannot build their types. `Region` is an AWS region's system name
(`eu-west-1`), read with `RegionEndpoint.GetBySystemName`, which does not check it against a list: a misspelled region
fails at the first connection, not at startup. `Credentials` becomes static credentials when both `AccessKey` and
`SecretKey` are set (session credentials when `SessionToken` is set too), and they apply whether or not `ServiceUri`
is set. Without them the default AWS credential chain applies, or, with a `ServiceUri` (LocalStack), placeholder
credentials.

The HTTP overload that takes an `HttpClient` never reads `BaseAddress`, since the client has one, but still reads
`BasePath` when its `basePath` argument is omitted.

### What configuration does not cover

Configuration supplies an endpoint's values. It never declares one, and it does not reach the rest of the library:

- **Endpoints and routes stay in code.** `AddEndpoint`, `RouteNamespace<T>()` and `RouteType<T>()` are typed
  declarations; there is no string form of a transport or a route to bind.
- **Pipelines and type filters stay in code.** `AddPipeline` takes a `Type`, and the type filter's list is declared
  with `AddTypeFilter`.
- **Durability, audit, jobs, resilience and the persistence engines read no configuration.** `AddDurability`,
  `AddAudit`, `AddJobs`, `AddResilience` and `UseSqlite` and its siblings read nothing from the `Conduit` section: set
  their options in their callbacks, and pass connection strings from wherever your code keeps them.
- **Nothing reloads.** Options bind once, during `AddConduit`, and are registered as plain singletons, not
  `IOptionsMonitor<T>`. A change to `appsettings.json` on a running host takes effect when it restarts.

## Attributes

All in `Conduit.Attributes` (`Conduit.NET.Contracts`).

| Attribute | Target | Purpose |
|---|---|---|
| `[LocalOnly]` | Class | The event never leaves the process: its local listeners run and no transport receives it. Inherited by a derived event. See [Security](security.md#localonly). |
| `[FireAndForget]` | Class | A void command or an event: its local handler or listeners run in the background, so the caller does not wait for them, and a routed command is sent without waiting for a reply. Invalid on `ICommand<TResult>` and `IQuery<TResult>` (CONDUIT003). Inherited. `Immediate = true` bypasses the outbox ([Outbox](outbox.md#the-immediate-escape-hatch)). |
| `[ClientVisible]` | Class | Emits a TypeScript type for the class. On an event, or a base of one, it is also what lets a SignalR hub push the event to browsers when no `IEventRecipientResolver` is registered. |
| `[ClosedSet("a", "b", …)]` | Class | States a closed set's wire values for TypeGen. The class implements `IClosedSet<TSelf>`, and the list must equal its `All` values, in order (checked when the type is first serialized). |
| `[DoNotAudit]` | Class, struct, property, record parameter | On a type, its dispatches are audited without a body. On a member, the body leaves that member out. A raw-JSON member (`JsonElement`, `JsonNode`, `object`, extension data) is otherwise captured verbatim, so mark it, type it with `[AuditAs]`, or suppress the type's payload. |
| `[AuditRedacted]` | Property, record parameter | The audited body holds `"•••"` for a present value (null and `""` as themselves), never the value. Not combinable with `[DoNotAudit]` (CONDUIT005). |
| `[AuditAs(nameof(Type))]` | Property, record parameter | On a `JsonElement` or `JsonElement?` member holding a serialized message whose type name is in the named sibling `string` property: audited as that message, redacted by its own marks. `"•••"` when the name is not a registered message type or the JSON does not read as it; left out when that type records no body. CONDUIT006 when misapplied. |
| `[NotAutoRegistered]` | Class | The generator does not register this handler or listener. For a listener a feature registers itself, as `AddAuditListener` does. On a command or query handler it leaves the message with no local handler (CONDUIT001). |
| `[assembly: RemoteContracts("Ns")]` | Assembly, repeatable | In the host project: suppresses CONDUIT001 for the commands and queries in that namespace and those beneath it, on whole segments, or for one type by its full name. |

## MSBuild properties (TypeGen)

Read by `Conduit.NET.TypeGen`'s build step. See [TypeScript Client](transports/typescript-client.md).

| Property | Default | Purpose |
|---|---|---|
| `ConduitTypeGenEnabled` | unset | Set to `true` to generate TypeScript after each build. |
| `ConduitTypeGenOutput` | `ClientApp/generated` | The output directory, relative to the project directory. |
| `ConduitTransports` | `Http;SignalR` | The transports `conduit.ts` includes. |
| `ConduitBasePath` | `/api/conduit` | The generated `HttpTransport`'s default base path: where the host maps `MapConduitEndpoint`. |
| `ConduitHubPath` | `/conduit` | The generated `SignalRTransport`'s default hub path: where the host maps `ConduitHub`. |
| `ConduitTypeGenAssemblyPath` | `$(TargetPath)` | The compiled assembly TypeGen reads. |

## Diagnostics

The source generator's diagnostics, CONDUIT001 to CONDUIT008, are listed with their causes and fixes in
[Diagnostics](diagnostics.md).

---

Previous: [Security](security.md) | [Home](../README.md) | Next: [FAQ](faq.md)
