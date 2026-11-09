# Conduit — Mediator Library Design

> **Historical document — original design spec.** This is the pre-implementation design that captures Conduit's *original* intent and rationale. It has drifted from the shipped library: it omits projects that now exist (`Conduit.Durability`, `Conduit.Audit`, `Conduit.Resilience`, the broker transports), frames Rebus-style messaging as "future" (broker transports are now first-class), and describes an Angular frontend (the real browser sample is Vite + TypeScript). It is retained for context and is **not** maintained as current reference.
>
> For the shipped reality, see the root [`CLAUDE.md`](../CLAUDE.md) (architecture, design decisions, project index), the per-project `CLAUDE.md` files under `src/`, and the guides in [`docs/`](.).

## Overview

Conduit is a lightweight mediator library for .NET that provides in-process command/query/event dispatch with source-generated routing, ASP.NET Core HTTP and SignalR transports, and TypeScript codegen for Angular frontends.

The library replaces MediatR (now commercial) with a custom implementation that is source-generated, reflection-free, and designed for vertical slice architecture with a unified frontend dispatch pattern.

## Goals

- In-process dispatch of commands, queries, and events with zero reflection at runtime (note: `GetType().Name` is used at the transport serialization boundary for event names — this is intentional and unavoidable at the JSON serialization layer)
- Single HTTP endpoint and SignalR hub method for frontend dispatch — transport is transparent to the caller
- TypeScript codegen that produces fully typed interfaces and type maps from C# types
- Unified event model: in-process listeners + SignalR push to frontend + future Rebus messaging
- Source-generated dispatch code, DI registration, and pipeline weaving
- Clean separation: the library provides interfaces and infrastructure, consumers define behaviors

## Non-Goals

- Rebus integration (Azure Service Bus and RabbitMQ transports are built-in; Rebus may be added in a future phase)
- Built-in pipeline behaviors (validation, auth, logging, OTel) — consumers compose their own
- npm package for Angular — TypeScript codegen produces everything the frontend needs
- Streaming/subscription queries — SignalR streaming is a separate concern outside Conduit

## Project Structure

```
Conduit/
├── src/
│   ├── Conduit/                             # Core interfaces and types
│   ├── Conduit.Analysis/                    # Shared type model (DispatchableTypeInfo, etc.)
│   ├── Conduit.SourceGen/                   # Roslyn source generator + RoslynScanner
│   ├── Conduit.Transport.Http/              # HTTP dispatch endpoint + HTTP transport
│   ├── Conduit.Transport.SignalR/           # SignalR hub + SignalR transport
│   ├── Conduit.Transport.AzureServiceBus/   # Azure Service Bus transport
│   ├── Conduit.Transport.RabbitMQ/          # RabbitMQ transport
│   ├── Conduit.Transport.AmazonSqs/         # Amazon SQS (+SNS) transport
│   ├── Conduit.Transport.ActiveMQ/          # ActiveMQ Artemis transport
│   └── Conduit.TypeGen/                     # MSBuild task for TypeScript emission + ReflectionScanner
├── tests/
│   ├── Conduit.Tests/
│   ├── Conduit.SourceGen.Tests/
│   ├── Conduit.TestUtils/                   # Shared test infrastructure
│   ├── Conduit.Transport.Http.Tests/
│   ├── Conduit.Transport.SignalR.Tests/
│   ├── Conduit.Transport.AzureServiceBus.Tests/
│   ├── Conduit.Transport.RabbitMQ.Tests/
│   ├── Conduit.Transport.AmazonSqs.Tests/
│   ├── Conduit.Transport.ActiveMQ.Tests/
│   └── Conduit.TypeGen.Tests/
├── samples/
│   └── ...
└── Conduit.slnx
```

### Dependency Graph

```
Conduit.SourceGen ──→ Conduit.Analysis ──→ (Roslyn APIs)
Conduit.TypeGen   ──→ Conduit.Analysis ──→ (Roslyn APIs)
Conduit.Transport.Http              ──→ Conduit ──→ MS.Extensions.DI.Abstractions
Conduit.Transport.SignalR           ──→ Conduit ──→ MS.Extensions.DI.Abstractions
Conduit.Transport.AzureServiceBus   ──→ Conduit + Azure.Messaging.ServiceBus
Conduit.Transport.RabbitMQ          ──→ Conduit + RabbitMQ.Client
Conduit.Transport.AmazonSqs         ──→ Conduit + AWSSDK.SimpleNotificationService + AWSSDK.SQS
Conduit.Transport.ActiveMQ          ──→ Conduit + AMQPNetLite.Core

Scanners live in their consumers:
  RoslynScanner      → Conduit.SourceGen (compile-time scanning)
  ReflectionScanner  → Conduit.TypeGen (post-compilation scanning)
```

`Conduit` (core) has no dependency on Analysis, SourceGen, TypeGen, or AspNetCore.

## Core Interfaces (`Conduit`)

### Marker Interfaces

```csharp
public interface ICommand
{
    void Validate() { }
}

public interface ICommand<TResult> : ICommand { }

public interface IQuery<TResult>
{
    void Validate() { }
}

public interface IEvent { }
```

- `ICommand` — void commands (no return value)
- `ICommand<TResult>` — commands that return a result
- `IQuery<TResult>` — queries that return data
- `IEvent` — events for pub/sub (in-process + transport delivery)
- `Validate()` is a default interface method (no-op). Commands override it for precondition guards. Business validation belongs in the handler.

### Handler Interfaces

```csharp
public interface ICommandHandler<TCommand> where TCommand : ICommand
{
    Task HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface ICommandHandler<TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

public interface IEventListener<TEvent> where TEvent : IEvent
{
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken);
}
```

- Handlers use constructor injection and are registered as transient
- `IEventListener` (not `IEventHandler`) to avoid collision with `System.EventHandler`

### Mediator Interface

```csharp
public interface IMediator
{
    Task ExecuteAsync(ICommand command, CancellationToken cancellationToken = default);
    Task<TResult> ExecuteAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);
    Task<TResult> FetchAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
    Task PublishAsync(IEvent @event, CancellationToken cancellationToken = default);
}
```

### Pipeline Behavior

```csharp
public interface IPipelineBehavior<TRequest, TResult>
{
    Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken);
}
```

Consumer-defined. The source generator weaves registered behaviors into a direct call chain (no runtime looping). Example behaviors consumers may implement: logging, authorization, OpenTelemetry tracing, validation.

#### Behavior Registration and Ordering

Consumers register behaviors via a builder API that defines global order:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddBehavior<LoggingBehavior>();   // runs first (outermost)
    conduit.AddBehavior<AuthBehavior>();      // runs second
    conduit.AddBehavior<ValidationBehavior>(); // runs third (closest to the handler among behaviors)
});
```

Order is determined by registration order. Behaviors apply globally to all commands and queries. Per-command behavior filtering can be done inside the behavior itself (e.g., check for an attribute on the request type).

The source generator reads the registered behavior types and weaves them into each dispatch method at compile time.

### Event Pipeline Behavior

```csharp
public interface IEventPipelineBehavior<TEvent> where TEvent : IEvent
{
    Task HandleAsync(TEvent @event, Func<Task> next, CancellationToken cancellationToken);
}
```

Separate from `IPipelineBehavior<TRequest, TResult>` because events have no result type and use continue-on-failure semantics. Each event listener gets its own inline try/catch — failures are collected and thrown as `EventListenerException` after all listeners run:

```csharp
// Generated — each listener wrapped in try/catch, exceptions collected
case OrderCreatedEvent e:
    Func<Task<object>> listenerChain = async () =>
    {
        try
        {
            await serviceProvider.GetRequiredService<SendConfirmationListener>()
                .HandleAsync(e, cancellationToken);
        }
        catch (Exception ex)
        {
            ConduitRuntime.LogListenerFailure(logger, "SendConfirmationListener", ex);
            exceptions.Add(ex);
        }
        try
        {
            await serviceProvider.GetRequiredService<UpdateInventoryListener>()
                .HandleAsync(e, cancellationToken);
        }
        catch (Exception ex)
        {
            ConduitRuntime.LogListenerFailure(logger, "UpdateInventoryListener", ex);
            exceptions.Add(ex);
        }
        return Unit.Value;
    };
    var behaviors = ConduitRuntime.ResolvePipelines<OrderCreatedEvent, object>(...);
    await ConduitRuntime.ExecuteWithBehaviors(e, behaviors, listenerChain, cancellationToken);
    break;
// After switch: if (exceptions.Count > 0) throw new EventListenerException(...)
```

A listener failure does not prevent other listeners from running. `PublishAsync` (local path) and `DispatchEventAsync` (wire path) both let it propagate: to the publisher, and to the transport to retry/dead-letter. Its `InnerException` is the first listener failure in run order. Only a `[FireAndForget]` event's background listeners catch and log it, as nothing awaits them.

Event behaviors are registered alongside command/query behaviors:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddBehavior<LoggingBehavior>();           // commands/queries
    conduit.AddEventBehavior<EventLoggingBehavior>(); // events
});
```

### Conduit Transport

```csharp
public interface IConduitTransport
{
    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken);
    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken);
}
```

Transports are bidirectional pipes between endpoints. Both methods use `DispatchEnvelope`:
- `SendRequestAsync` — request/response dispatch (outbound commands/queries)
- `SendEventAsync` — fire-and-forget delivery (outbound events)

When `PublishAsync` is called, the mediator:
1. Runs all in-process `IEventListener<T>` implementations (continue on failure)
2. If the event is NOT `[LocalOnly]`, broadcasts to all DI-registered `IConduitTransport` instances (server-mode transports)

### Wire Mediator

```csharp
public interface IWireMediator : IMediator
{
    Task<DispatchResult<object>> HandleWireRequestAsync(
        DispatchEnvelope envelope, CancellationToken cancellationToken);
    Task HandleWireEventAsync(
        DispatchEnvelope envelope, CancellationToken cancellationToken);
}
```

Extended mediator interface for inbound wire dispatch. Transport packages (HTTP, SignalR) inject `IWireMediator`; application code uses `IMediator`. `HandleWireRequestAsync` chains registered `IInboundInterceptor` instances, deserializes the payload, dispatches to the handler, and wraps the result in `DispatchResult<T>`. `HandleWireEventAsync` does the same for events — lets `EventListenerException` propagate to the transport for retry/dead-letter, swallows other exceptions.

### Message Context

`MessageContext` tracks causation through dispatch chains via `AsyncLocal`. Every dispatch gets a unique `MessageId`. A head/origin message self-causates: `CausationId == MessageId == ConversationId`. `CausationId` is never null (it is a non-nullable `string`). Nested dispatches inherit `ConversationId` (the root message's ID) and set `CausationId` to the parent's `MessageId`.

```
CreateOrderCommand    (MessageId=A, CausationId=A, ConversationId=A)   ← head self-causates
  ├─ OrderCreatedEvent  (MessageId=B, CausationId=A, ConversationId=A)
  └─ AuditLogEvent      (MessageId=C, CausationId=A, ConversationId=A)
       └─ NotifyCommand  (MessageId=D, CausationId=C, ConversationId=A)
```

Three factory methods:
- `MessageContext.StartDispatch()` — local dispatch, generates IDs, inherits from parent if nested
- `MessageContext.StartDispatchFromTransport(messageId, causationId, conversationId)` — inbound wire dispatch, all values from broker message properties
- `MessageContext.StartLocalDelivery()` — the outbox's local delivery of an entry: a nested dispatch whose `DeliveredMessageId` is the entry's `MessageId`

`DeliveredMessageId` is the id of the message whose delivery a dispatch is. It equals `MessageId` everywhere except inside the outbox's local delivery, where each attempt has a fresh `MessageId` but every attempt delivers the same entry — so a redelivery repeats it, and once-per-message work (a Conduit.Jobs trigger's run key) keys on it. A nested dispatch does not inherit it.

Access via `MessageContext.Current` (static property). Set automatically by the generated mediator and `ConduitRuntime` wire dispatch. Context is set before the `IInboundInterceptor` chain runs.

### Endpoint-Based Routing (Commands and Queries Only)

Routes assign a destination endpoint for request/response messages. Events don't route — they broadcast to all DI-registered transports. The producer configures routes because it owns the deployment topology.

```csharp
public sealed class ConduitEndpoint
{
    public required string Name { get; init; }
    // Transport is resolved from DI as a keyed IConduitTransport (key = Name)
}

public sealed class ConduitRoute
{
    public required string Pattern { get; init; }   // namespace prefix
    public required string EndpointName { get; init; }
}

public sealed class ConduitOptions
{
    public Dictionary<string, ConduitEndpoint> Endpoints { get; } = new();
    public List<ConduitRoute> Routes { get; } = [];
    public List<Type> PipelineTypes { get; } = [];
    public string SystemName { get; set; } = "conduit";
    public string? ApplicationName { get; set; }
    public TopologyMode TopologyMode { get; set; } = TopologyMode.Production;
    public string? TopologyPrefix { get; set; }  // auto-resolves: Developer→UserName, Test→random, Production→null
    public bool ProducerOnly { get; set; }
}
```

`ConduitRuntime.ResolveRoute` checks the route table before local dispatch. If a message type matches a route, the generated mediator resolves the transport via `ConduitRuntime.ResolveTransport(serviceProvider, endpoint)` (keyed DI lookup), serializes the request into a `DispatchEnvelope`, and sends it via the transport. If no route matches, dispatch is local.

Configuration uses a fluent builder API (runtime only — no attribute-based routing):

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("inventory", endpoint =>
    {
        endpoint.UseSignalR(new Uri(config["InventoryServiceUrl"]));
        endpoint.RouteNamespace<Inventory.Commands.ReserveStock>();
    });
});
```

### Event Broadcast

Events broadcast — they don't route. `PublishAsync` dispatches to local listeners then broadcasts to all DI-registered `IConduitTransport` instances. Events marked `[LocalOnly]` skip the broadcast step entirely (compile-time, zero runtime cost).

```csharp
[LocalOnly]
public record UserPasswordChangedEvent(Guid UserId) : IEvent;
```

Inbound filtering is handled by `TypeFilterInterceptor` (rejects disallowed types arriving from transports with `ForbiddenException`). `[LocalOnly]` is the outbound complement — preventing events from leaving.

### Topology Naming

`TopologyNaming` is a static class in `Conduit.Configuration` that generates all broker resource names consistently across transports. It provides two families of methods:

- `CreateSharedResourceName(options, maxLength)` — names for resources shared across apps (topic/exchange, unicast subscriptions). Format: `{prefix}-{systemName}` or just `{systemName}` in Production mode.
- `CreateAppResourceName(options, maxLength)` — names scoped to a specific app (queues, multicast subscriptions). Format: `{prefix}-{systemName}-{appName}` or `{systemName}-{appName}` in Production mode.

Both families accept an optional `suffix` parameter for sub-resources (e.g. `"replies"`, pool names, type names). Type name suffixes containing dots are reversed and hyphenated (e.g. `Orders.Commands.CreateOrder` becomes `CreateOrder-Commands-Orders`). Names exceeding `maxLength` are truncated with an xxHash32 suffix to preserve uniqueness.

### Topology Modes

`TopologyMode` controls resource naming, durability, and lifetime:

| Mode | Prefix | Durability | Auto-delete |
|---|---|---|---|
| `Production` | None | Durable | Never |
| `Developer` | `{TopologyPrefix}` (default: `Environment.UserName`) | Non-durable | 4 hours |
| `Test` | `{TopologyPrefix}` (default: `test-{random}`) | Non-durable | 5 minutes |

Developer mode gives each developer isolated broker resources (e.g. `bpage-conduit` instead of `conduit`). Test mode creates fully isolated, short-lived resources for integration tests that self-clean.

`ConduitOptions.TopologyPrefix` auto-resolves on first access: Developer defaults to `Environment.UserName`, Test generates a random 13-character prefix, Production resolves to null. Explicitly setting `TopologyPrefix` overrides the auto-resolution — use this when multiple hosts must share the same topology (e.g., multi-host integration tests).

Configuration:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.TopologyMode = TopologyMode.Developer;
    conduit.TopologyPrefix = "myprefix";  // optional, defaults to Environment.UserName
    conduit.SystemName = "myapp";         // optional, defaults to "conduit"

    // For integration tests: share topology prefix across hosts
    conduit.TopologyMode = TopologyMode.Test;
    conduit.TopologyPrefix = "shared-test-prefix";  // optional, defaults to random
});
```

### ProducerOnly Mode

`conduit.ProducerOnly = true` configures a host to only publish messages without creating inbound topology or starting consumers.

**What it enables:** `ConnectAsync`, `EnsureOutboundTopologyAsync` (reply queue/topic only), outbound transport (`SendRequestAsync`, `SendEventAsync`)

**What it disables:** `EnsureTopologyAsync` (inbound subscriptions, bindings, consumer queues), `StartConsumersAsync` (message receivers)

**Use cases:**
- API hosts that route commands to worker services but don't handle them locally
- Microservices that only publish events (no inbound handlers)
- Test sender hosts that must not create shared subscriptions (prevents topology conflicts)

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.ProducerOnly = true;
    conduit.AddEndpoint("workers", ep =>
    {
        ep.UseRabbitMQ(connectionString);
        ep.RouteNamespace<MyApp.Commands.CreateOrder>();
    });
});
```

### Topic-Based Message Routing (Broker Transports)

All broker transports use a unified topic-based routing model. All message types — commands, queries, and events — publish to a single shared topic/exchange/address. Subscriptions and queue bindings route messages from the topic to the appropriate app queue. Reply messages are the only exception; they bypass the topic and go point-to-point.

**Azure Service Bus topology:**
```
Topic: {systemName}
├── Subscription: {ReversedTypeName}
│   (unicast — command/query, SQL filter: Subject = '{FullTypeName}',
│    forwards to handler's app queue)
├── Subscription: {appName}-{ReversedTypeName}
│   (multicast — event, SQL filter: Subject = '{FullTypeName}',
│    forwards to subscribing app's queue)
Queue: {systemName}-{appName}
Queue: {systemName}-{appName}-replies (session-enabled)
Queue: {systemName}-{appName}-{poolName} (optional)
```

**RabbitMQ topology:**
```
Exchange: {systemName} (type: topic)
Queue: {systemName}-{appName}
├── Binding: routing key = {FullTypeName} (per handled type)
Queue: {systemName}-{appName}-{poolName}
├── Binding: routing key = {FullTypeName} (per pool type)
Reply Queue: exclusive auto-delete (per connection)
```

**Amazon SQS (+SNS) topology:**
```
SNS Topic: {systemName}
├── Subscription → SQS Queue (shared name, filter: Type = '{FullTypeName}', unicast)
├── Subscription → SQS Queue (per-app name, filter: Type = '{FullTypeName}', multicast)
SQS Queue: {systemName}-{appName} (app queue)
SQS Queue: {systemName}-{appName}-replies (reply queue, direct send)
SQS Queue: {systemName}-{appName}-{poolName} (optional)
```

**ActiveMQ Artemis topology:**
```
Anycast Address: {systemName} (commands/queries)
├── Queue: {ReversedTypeName} (shared name — unicast)
Multicast Address: {systemName}-events
├── Queue: {appName}-{ReversedTypeName} (per-app — multicast)
Reply Queue: per-instance exclusive temporary queue
```

Unicast correctness for commands/queries in RabbitMQ relies on only one app binding/subscribing a given type. ASB enforces unicast via shared subscription names. SQS uses shared subscription names for filter policies. ActiveMQ uses shared queue names on the anycast address.

### Error Model

Exceptions flow naturally through .NET. The dispatch endpoint (HTTP/SignalR) is the single mapping point to a structured envelope for the wire.

```csharp
public record ErrorCode(string Value, int HttpStatusCode)
{
    public static readonly ErrorCode NotFound = new("NotFound", 404);
    public static readonly ErrorCode Unauthorized = new("Unauthorized", 401);
    public static readonly ErrorCode Conflict = new("Conflict", 409);
    public static readonly ErrorCode InternalError = new("InternalError", 500);
    public static readonly ErrorCode BadRequest = new("BadRequest", 400);
}

public abstract class ConduitException(ErrorCode code, string message)
    : Exception(message)
{
    public ErrorCode Code => code;
}

// Built-in subclasses
public sealed class NotFoundException(string message)
    : ConduitException(ErrorCode.NotFound, message);

public sealed class UnauthorizedException(string message)
    : ConduitException(ErrorCode.Unauthorized, message);

public sealed class ConflictException(string message)
    : ConduitException(ErrorCode.Conflict, message);
```

`ErrorCode` is an enumeration class — equality is based on `Value` only. The `HttpStatusCode` is used by RESTful endpoints for Problem Details responses. On the wire (`DispatchError.Code`), only `Value` is serialized.

Consumers extend `ConduitException` with domain-specific error codes:

```csharp
public static readonly ErrorCode InsufficientCredit = new("InsufficientCredit", 422);

public class InsufficientCreditException(string message)
    : ConduitException(InsufficientCredit, message);
```

### Wire Envelope

```csharp
public sealed record DispatchEnvelope(string Type, JsonElement Payload);

public sealed record DispatchResult<T>
{
    public T? Data { get; init; }
    public DispatchError? Error { get; init; }
}

public sealed record DispatchError(
    string Code,
    string Message,
    DispatchErrorDetail[]? Details = null);

public sealed record DispatchErrorDetail(string? Field, string Message);
```

The frontend discriminates on `error` presence — `success` boolean is intentionally omitted as redundant.

### Discoverability Attributes

```csharp
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class DispatchableNamespaceAttribute(string ns) : Attribute
{
    public string Namespace => ns;
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class DispatchableAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Class)]
public sealed class NotDispatchableAttribute : Attribute { }
```

Resolution priority: `[NotDispatchable]` wins over namespace match, which wins over no annotation.

`[DispatchableNamespace]` declarations also drive TypeScript file segmentation — each declaration produces a separate `.generated.ts` file.

### Remote Contract Suppression

When a service references a shared contracts library containing commands/queries handled by another service, the source generator emits CONDUIT001 ("no handler found") for those types. To suppress this without blanket-disabling the diagnostic:

```csharp
[assembly: RemoteContracts("Conduit.Sample.Inventory")]
```

The generator skips CONDUIT001 for types in a declared `[RemoteContracts]` namespace or a namespace beneath it, matched on whole segments (`"Inventory"` covers `Inventory.Commands`, not `InventoryAudit`); a type's full name matches that type. Types outside declared namespaces still produce the warning if they lack a handler.

## Source Generator (`Conduit.SourceGen`)

Roslyn incremental source generator that uses `Conduit.Analysis` to scan for dispatchable types and emits:

### 1. Dispatch Methods

One static method per command/query with direct calls — no reflection, no dynamic:

```csharp
[GeneratedCode("Conduit.SourceGen", "1.0.0")]
internal static class ConduitDispatch
{
    public static async Task<Guid> Execute(
        CreateOrderCommand command,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        // No behaviors registered: the innermost step is the handler step itself, so Validate() runs immediately before the handler.
        command.Validate();
        var handler = serviceProvider.GetRequiredService<ICommandHandler<CreateOrderCommand, Guid>>();
        return await handler.HandleAsync(command, cancellationToken);
    }
}
```

### 2. Pipeline Weaving

When behaviors are registered, the generator composes them inline:

```csharp
public static async Task<Guid> Execute(
    CreateOrderCommand command,
    IServiceProvider serviceProvider,
    CancellationToken cancellationToken)
{
    var behavior1 = serviceProvider.GetRequiredService<LoggingBehavior<CreateOrderCommand, Guid>>();
    var behavior2 = serviceProvider.GetRequiredService<AuthBehavior<CreateOrderCommand, Guid>>();

    return await behavior1.HandleAsync(command,
        () => behavior2.HandleAsync(command,
            () =>
            {
                command.Validate();
                var handler = serviceProvider.GetRequiredService<ICommandHandler<CreateOrderCommand, Guid>>();
                return handler.HandleAsync(command, cancellationToken);
            },
            cancellationToken),
        cancellationToken);
}
```

`Validate()` is part of the handler step, not a step before the pipeline: every behavior — authorization included — runs before a message's input is validated, and every dispatch path (command, query, void, fire-and-forget, wire) still validates before its handler runs. On the fire-and-forget path the handler step is the enqueue, so `Validate()` runs on the caller's thread and an invalid command is refused to its caller and never enqueued.

### 3. DI Registration

Handlers and listeners are registered by their interface types. This enables test substitution and service bus listener discovery.

```csharp
public static IServiceCollection AddConduitHandlers(this IServiceCollection services)
{
    services.AddTransient<ICommandHandler<CreateOrderCommand, Guid>, CreateOrderHandler>();
    services.AddTransient<ICommandHandler<DeleteOrderCommand>, DeleteOrderHandler>();
    services.AddTransient<IEventListener<OrderCreatedEvent>, OrderCreatedListener>();
    services.AddTransient<ConduitMediator>();
    services.AddTransient<IMediator>(sp => sp.GetRequiredService<ConduitMediator>());
    services.AddTransient<IWireMediator>(sp => sp.GetRequiredService<ConduitMediator>());
    return services;
}
```

All registrations are **transient**. `ConduitMediator` is stateless — it resolves handlers from `IServiceProvider` per dispatch call. Transient registration allows `IMediator` to be injected into singletons (e.g., `BackgroundService`) without scope conflicts. Handlers still resolve from the correct scope in ASP.NET Core request pipelines because the `IServiceProvider` injected into the mediator is scope-aware.

### 4. Mediator Implementation

The generated `ConduitMediator` implements `IWireMediator` (which extends `IMediator`). Key behaviors:

- **Strongly-typed overloads** per command/query — compiler picks these, zero boxing
- **Route-aware dispatch** — checks `ConduitRuntime.ResolveRoute` before local dispatch; if a route matches, resolves the transport via `ConduitRuntime.ResolveTransport` (keyed DI) and sends via `DispatchEnvelope`
- **Event broadcast** — `PublishAsync` dispatches to local listeners via `PublishLocalAsync`, then broadcasts to all DI-registered `IConduitTransport` instances (unless `[LocalOnly]`)
- **Wire dispatch** — `HandleWireRequestAsync`/`HandleWireEventAsync` chain `IInboundInterceptor` instances, deserialize by type name, and dispatch locally
- **Pipeline behaviors** — resolved via `ConduitRuntime.ResolvePipelines` in registration order (first = outermost)

### 5. REST Endpoint Mappings (conditional)

When the compilation references `Conduit.Transport.Http`, the generator emits an `internal` `ConduitRestEndpoints` with `MapConduitGet<T>`, `MapConduitPost<T>`, etc. — compile-time type switches that delegate to `ConduitRestHandlers`. It is internal because every assembly that sees the transport emits one into `Microsoft.AspNetCore.Builder`: a public class in a module library would make its host's calls ambiguous.

### Compile-Time Diagnostics

The generator emits diagnostics for:
- `CONDUIT001` — Command or query with no matching handler (suppressed by `[assembly: RemoteContracts("Ns")]` for remote contract types)
- `CONDUIT002` — Multiple handlers for the same command or query
- `CONDUIT004` — Missing `[Authorize]` or `[AllowAnonymous]` on a dispatchable type

## HTTP Integration (`Conduit.Transport.Http`)

### Wire Dispatch Endpoint

`MapConduitEndpoint()` maps two POST endpoints for Conduit-aware consumers (service-to-service, TypeScript client):
- `POST /api/conduit/dispatch` — commands and queries (returns `DispatchResult<object>`)
- `POST /api/conduit/publish` — events (fire-and-forget, returns 200 OK)

The message type is sent in the `X-Conduit-Type` request header. The request body contains the plain serialized message JSON — no envelope wrapper. This matches the broker wire format (type in transport-native properties, body is the raw message). `MessageContext` is propagated via `X-Conduit-Message-Id`, `X-Conduit-Causation-Id`, `X-Conduit-Conversation-Id` headers (C# clients), which the server honors only on an endpoint mapped with `trustCallerContext: true` and only when each id is a plain token (`MessageContextIds.IsValid`). Browser clients never send them, and everywhere else the server creates a fresh context.

Delegates to `IWireMediator.HandleWireRequestAsync` / `HandleWireEventAsync`. Error codes map to HTTP status codes via `ErrorCode.ToHttpStatusCode()`. Returns `DispatchResult` response format.

### RESTful Endpoint Mapping

Maps individual HTTP routes to Conduit message types — conventional REST for external consumers. Coexists with the envelope-based dispatch endpoint above. The source generator emits `MapConduitGet<T>`, `MapConduitPost<T>`, `MapConduitPut<T>`, `MapConduitDelete<T>`, `MapConduitPatch<T>`, and a generic `MapConduit<T>(method, path)` escape hatch:

```csharp
app.MapConduitGet<GetItemQuery>("/api/items/{id}");
app.MapConduitPost<CreateItemCommand>("/api/items");
app.MapConduitPut<UpdateItemCommand>("/api/items/{id}");
app.MapConduitDelete<DeleteItemCommand>("/api/items/{id}");
```

Each returns `RouteHandlerBuilder`, so standard ASP.NET Core endpoint metadata chains naturally (auth, OpenAPI, rate limiting, etc.):

```csharp
app.MapConduitGet<GetItemQuery>("/api/items/{id}")
    .RequireAuthorization("ItemReader")
    .WithTags("Items")
    .ProducesProblem(404);
```

**Binding strategy:**
- Queries use `[AsParameters]` — bind from route values and query strings (GET/DELETE)
- Commands bind the entire type from the JSON request body (POST/PUT/PATCH)

**Response behavior:**
- `IQuery<TResult>` / `ICommand<TResult>` → 200 OK with result
- `ICommand` (void) → 204 No Content
- `ConduitException` → Problem Details (RFC 9457) with status from `ErrorCode.HttpStatusCode`

**Implementation:** Two-layer architecture. `ConduitRestHandlers` in `Conduit.Transport.Http` provides typed handler helpers. The source generator emits `ConduitRestEndpoints` with compile-time type switches that delegate to the helpers — zero per-request reflection. Generated only when the compilation references `Conduit.Transport.Http`.

**Verb-to-message restrictions:** `MapConduitGet` accepts queries only. All other verbs accept any command or query type.

## SignalR Integration (`Conduit.Transport.SignalR`)

### SignalR Hub

`ConduitHub` handles client-to-server dispatch/publish and server-to-client event delivery. `Dispatch` and `Publish` methods delegate to `IWireMediator`.

### SignalR Transport

Dual-mode transport implementing `IConduitTransport`:
- **Server mode** (`IHubContext<ConduitHub>`) — pushes events to connected clients via hub broadcast. Registered in DI by `AddConduitSignalR()`.
- **Client mode** (`HubConnection`) — sends commands/queries to a remote hub, receives events back on the same connection. Uses lazy connect (`EnsureConnectedAsync`).

### HTTP Transport

Sends commands and queries to a remote Conduit endpoint via HTTP POST. The message type is sent in the `X-Conduit-Type` header and the body contains the plain serialized message — matching the broker wire format. `MessageContext` values are sent via `X-Conduit-*` headers for cross-service propagation.

```csharp
public class HttpTransport(HttpClient httpClient, string basePath = "/api/conduit") : IConduitTransport
{
    public async Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, basePath + "/dispatch")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(envelope.Payload, ConduitRuntime.JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Conduit-Type", envelope.Type);
        // MessageContext headers added when present...
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<DispatchResult<JsonElement?>>(cancellationToken);
        return ConduitRuntime.UnwrapReply(result);
    }
}
```

### Registration

```csharp
// Program.cs
builder.Services.AddConduit(conduit =>       // generated — registers handlers, mediator, core infra
{
    conduit.AddEndpoint("inventory", ep =>
    {
        ep.UseSignalR(new Uri(config["InventoryUrl"]));
        ep.RouteNamespace<Inventory.Commands.ReserveStock>();
    });
    conduit.AddPipeline(typeof(LoggingBehavior<,>));
});
builder.Services.AddConduitSignalR();        // registers SignalR hub + server-mode transport

app.MapConduitEndpoint();                    // envelope dispatch for Conduit-aware consumers
app.MapHub<ConduitHub>("/conduit");           // SignalR hub

// RESTful endpoints for external consumers
app.MapConduitGet<GetItemQuery>("/api/items/{id}");
app.MapConduitPost<CreateItemCommand>("/api/items");
```

## TypeScript Codegen (`Conduit.TypeGen`)

MSBuild task that runs on `dotnet build`. Uses `MetadataLoadContext` to scan the compiled assembly for `[ClientVisible]` types, then recursively discovers all transitively-referenced complex types (including types from referenced assemblies like a Domain layer) and emits a self-contained TypeScript client runtime — no npm package needed.

### Configuration

```xml
<PropertyGroup>
  <ConduitTypeGenEnabled>true</ConduitTypeGenEnabled>
  <!-- Optional overrides: -->
  <!-- <ConduitTypeGenOutput>src/generated</ConduitTypeGenOutput> -->  <!-- default: ClientApp/generated -->
  <!-- <ConduitTransports>Http</ConduitTransports> -->                 <!-- default: Http;SignalR -->
</PropertyGroup>

<Import Project="path/to/Conduit.TypeGen.targets" />
```

The `.targets` file supports both NuGet package and project-reference layouts via `ConduitTypeGenTaskAssembly`.

### Output Structure

Two files:

```
ClientApp/generated/
├── conduit.ts              # Stable runtime: types, transport interface, HttpTransport, SignalRTransport
└── conduit.generated.ts    # Per-project: interfaces, type maps, ConduitClient, re-exports
```

### Runtime File (conduit.ts)

Identical across all projects (modulo transport selection). Contains:

```typescript
// Base types
export interface DispatchResult<T> { data?: T; error?: ConduitError; }
export interface ConduitError { code: string; message: string; details?: ConduitErrorDetail[]; }
export interface ConduitErrorDetail { field?: string; message: string; }

// Transport interface (mirrors C# IConduitTransport)
export interface ConduitTransport {
  sendRequest<TResult>(type: string, payload: unknown): Promise<DispatchResult<TResult>>;
  sendEvent(type: string, data: unknown): Promise<void>;
  subscribe(eventName: string, handler: (data: unknown) => void): Subscription;
}
export interface Subscription { unsubscribe(): void; }

// HttpTransport — native fetch, zero npm dependencies
export class HttpTransport implements ConduitTransport { /* ... */ }

// SignalRTransport — wraps HubConnection (conditionally emitted)
export class SignalRTransport implements ConduitTransport { /* ... */ }
```

`SignalRTransport` and its `@microsoft/signalr` import are omitted when `<ConduitTransports>Http</ConduitTransports>`.

### Generated File (conduit.generated.ts)

Per-project types, maps, and typed client:

```typescript
import type { ConduitTransport, DispatchResult, Subscription } from './conduit';

// Type interfaces (from [ClientVisible] types)
export interface CreateNoteCommand { text: string; }
export interface NoteDto { id: string; text: string; }

// Type maps (keys are FullTypeName to match C# wire discriminator)
export interface CommandMap {
  'MyApp.Commands.CreateNoteCommand': { request: CreateNoteCommand; response: string };
  'MyApp.Commands.DeleteNoteCommand': { request: DeleteNoteCommand; response: void };
}
export interface QueryMap {
  'MyApp.Queries.GetNotesQuery': { request: GetNotesQuery; response: NoteDto[] };
}
export interface EventMap {
  'MyApp.Events.NoteCreatedEvent': NoteCreatedEvent;
}

// Typed client (not a mediator — thin dispatch client)
export class ConduitClient {
  constructor(private transport: ConduitTransport) {}

  execute<K extends keyof AllCommands>(type: K, request: AllCommands[K]['request'])
    : Promise<DispatchResult<AllCommands[K]['response']>> { /* ... */ }

  fetch<K extends keyof AllQueries>(type: K, request: AllQueries[K]['request'])
    : Promise<DispatchResult<AllQueries[K]['response']>> { /* ... */ }

  publish<K extends keyof AllEvents>(type: K, data: AllEvents[K])
    : Promise<void> { /* ... */ }

  on<K extends keyof AllEvents>(event: K, handler: (data: AllEvents[K]) => void)
    : Subscription { /* ... */ }
}

// Re-exports
export { HttpTransport, SignalRTransport } from './conduit';
export type { ConduitTransport, DispatchResult, ConduitError, ConduitErrorDetail, Subscription } from './conduit';
```

Key design decisions:
- `ConduitClient` returns `DispatchResult<T>` (does not throw) — consumers decide error handling
- Void commands merged into `CommandMap` with `response: void` (no separate `VoidCommandMap`)
- Type map keys use `FullTypeName` to match the C# wire discriminator
- Non-dispatchable `[ClientVisible]` types (DTOs) get interfaces but don't appear in type maps
- **Recursive type discovery**: TypeGen walks the property type graph of `[ClientVisible]` types and query/command result types, automatically discovering and emitting interfaces for all transitively-referenced complex types — including types from referenced assemblies (e.g., Domain layer DTOs) that don't have `[ClientVisible]` themselves. Only `[ClientVisible]` is needed on the root dispatchable types; their property types are included automatically.

### Usage

```typescript
import { ConduitClient, SignalRTransport } from '../generated/conduit.generated';

const transport = new SignalRTransport();
const conduit = new ConduitClient(transport);
await transport.start();

// Fully typed — IDE autocomplete, compile-time checking
const result = await conduit.execute('MyApp.Commands.CreateNoteCommand', { text: 'hello' });
const notes = await conduit.fetch('MyApp.Queries.GetNotesQuery', {});
conduit.on('MyApp.Events.NoteCreatedEvent', event => console.log(event.text));
```

### C# → TypeScript Type Mapping

| C# | TypeScript |
|----|-----------|
| `string`, `Guid` | `string` |
| `int`, `long`, `short` | `number` |
| `decimal`, `double`, `float` | `number` |
| `bool` | `boolean` |
| `DateTime`, `DateTimeOffset` | `string` (ISO 8601) |
| `T[]`, `List<T>`, `IList<T>`, `IEnumerable<T>` | `T[]` |
| `Dictionary<string, T>` | `Record<string, T>` |
| `T?` (nullable) | `T \| null` |
| Enums | String literal union |

## Validation Strategy

Two-tier validation:

1. **Command `Validate()` method** — precondition guards. Fast fail on malformed requests: the generated dispatch calls `Validate()` immediately before the handler, so a malformed request never reaches it — and after every pipeline behavior, so authorization refuses a caller before their input is validated. These are developer mistakes, not user errors. Single throw on first failure.

```csharp
public record CreateOrderCommand(string CustomerId, List<LineItem> Items) : ICommand<Guid>
{
    public void Validate()
    {
        if (string.IsNullOrEmpty(CustomerId))
            throw new ValidationException("CustomerId is required");
        if (Items is not { Count: > 0 })
            throw new ValidationException("At least one item is required");
    }
}
```

2. **Handler business validation** — domain rule enforcement. These produce meaningful errors for users. Handlers throw `ConduitException` subclasses.

```csharp
public class CreateOrderHandler(IDbContext db) : ICommandHandler<CreateOrderCommand, Guid>
{
    public async Task<Guid> HandleAsync(
        CreateOrderCommand command,
        CancellationToken cancellationToken)
    {
        var customer = await db.Customers.FindAsync(command.CustomerId, cancellationToken)
            ?? throw new NotFoundException($"Customer {command.CustomerId} not found");

        if (!customer.IsActive)
            throw new ConflictException("Customer account is inactive");

        // ... create order
    }
}
```

## Event Model

Unified pub/sub across transports. Events flow in both directions:

**Outbound** (C# → external):
```
mediator.PublishAsync(new OrderCreatedEvent(...))
    │
    ├─ In-process: IEventListener<OrderCreatedEvent> handlers (continue on failure)
    ├─ SignalR: SignalRTransport → hub.Clients.All.SendAsync("OrderCreated", ...)
    └─ Rebus (future): RebusTransport → bus.Publish(...)
```

**Inbound** (external → C#):
```
TypeScript client calls hub.invoke("Publish", "OrderCreated", payload)
    │
    └─ ConduitHub.Publish → IWireMediator.HandleWireEventAsync(envelope)
        └─ Deserialize → PublishLocalAsync(IEvent) → listeners only (no re-broadcast)
```

The publisher doesn't know or care who's listening or which transports are configured.

## Testing

**Framework:** MSTest v4 + AwesomeAssertions. No mocking libraries — tests use real types, stubs, or in-memory state.

**Shared test infrastructure** in `tests/Conduit.TestUtils/`:
- `TestHost` — lightweight `IHost` wrapper that starts hosted services. `DisposeAsync` is idempotent.
- `EventSpy<T>` — event listener spy for capturing published events
- `SpyLog` — thread-safe invocation recorder with `WaitForCountAsync`
- `ContainerHelper` — static utilities for container cleanup, reuse, and port discovery
- `MessageContextSnapshot` — captures `MessageId`, `CausationId`, `ConversationId`

**Per-transport base classes** (`RabbitMqTestBase`, `AsbTestBase`, `SqsTestBase`, `ActiveMQTestBase`):
- Own container lifecycle as static state, initialized via `[AssemblyInitialize]` delegate from `AssemblySetup`
- Provide `CreateHostAsync` and `CreateSenderHostAsync` factory methods
- `[TestInitialize]` guards with `Assert.Inconclusive` when broker unavailable
- All hosts must use `await using` — sole disposal mechanism (no host tracking)

**Transport test matrices** documented in `docs/transport-test-matrices.md` — defines the required tests for every broker transport (28 behavior tests + 16 universal topology tests + transport-specific tests). New transports MUST implement the full matrix.

### Handler Override

Since `AddConduit()` registers handlers in `IServiceCollection`, you can override any handler after registration. DI last-registration-wins:

```csharp
builder.Services.AddConduit();

// Override a command handler with a mock
builder.Services.AddTransient<ICommandHandler<CreateOrder, OrderResult>, MockCreateOrderHandler>();

// Replace an event listener entirely
builder.Services.RemoveAll<IEventListener<OrderCreated>>();
builder.Services.AddTransient<IEventListener<OrderCreated>, TestOrderCreatedListener>();
```

No test harness or framework API is needed — standard .NET DI manipulation between `AddConduit()` and `Build()`.

### Multi-Host Integration Tests

Use `ProducerOnly = true` on sender hosts to prevent topology conflicts:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.ApplicationName = "sender";
    conduit.ProducerOnly = true;  // no inbound topology, no consumers
    conduit.AddEndpoint("handler", ep =>
    {
        ep.UseRabbitMQ(connectionString);
        ep.RouteNamespace<MyApp.Commands.CreateOrder>();
    });
});
```

Use `TopologyMode.Test` with a shared `TopologyPrefix` so all hosts in a test share the same broker resources:

```csharp
conduit.TopologyMode = TopologyMode.Test;
conduit.TopologyPrefix = sharedPrefix;  // same value across all hosts in the test
```

### Test Coverage by Project

**Conduit.Tests:**
- `ConduitException` subclasses produce correct codes and status codes
- `Validate()` throws on invalid commands, no-ops on valid

**Conduit.SourceGen.Tests:**
- Generated dispatch routes to correct handler
- Pipeline behaviors woven in correct order
- Event pipeline behaviors woven per-listener with inline try/catch
- Void and result commands generate correct overloads
- Missing handler produces compile-time diagnostic
- Missing `[Authorize]`/`[AllowAnonymous]` on dispatchable type produces diagnostic

**Conduit.Transport.Http.Tests:**
- Integration tests with full HTTP pipeline
- HTTP POST dispatch returns correct envelope for success, `ConduitException`, unexpected exception

**Conduit.Transport.SignalR.Tests:**
- SignalR dispatch returns correct envelope
- `SignalRTransport` delivers events to connected clients

**Conduit.Transport.{AzureServiceBus,RabbitMQ,AmazonSqs,ActiveMQ}.Tests:**
- Multi-host integration tests per transport test matrix (command/query/event routing, competing instances, MessageContext propagation, receiver pools, topology modes)
- Unit tests for topology managers (resource naming, unicast/multicast routing, pool routing, topology modes)

**Conduit.TypeGen.Tests:**
- C# type → TypeScript mapping for all supported types
- Namespace segmentation produces correct files
- Merge file composition
- Generated TypeScript compiles (`tsc --noEmit`)

## Broker Transport Base Class (`BrokerTransportBase<TMessage>`)

All 4 broker transports (Azure Service Bus, RabbitMQ, Amazon SQS, ActiveMQ) share a common lifecycle and message processing pattern. `BrokerTransportBase<TMessage>` in `Conduit.Runtime` uses the template method pattern to own the shared orchestration while transports implement ~15 abstract methods for transport-specific primitives.

**Template methods (not overridable):**
- `StartAsync` — branches on `ProducerOnly`: true → `ConnectAsync` + `EnsureOutboundTopologyAsync`; false → `ConnectAsync` + resolves `ConduitTypeRegistry` + `EnsureTopologyAsync` + `StartConsumersAsync`
- `StopAsync` — calls `StopConsumersAsync`
- `SendRequestAsync` — serializes to `BinaryData`, calls `PublishRequestAsync`, deserializes reply, calls `ConduitRuntime.UnwrapReply`
- `SendEventAsync` — serializes to `BinaryData`, calls `PublishEventAsync`
- `ProcessInboundMessageAsync` — full inbound dispatch: save/restore `MessageContext`, create DI scope, resolve `IWireMediator`, deserialize, branch request/event, handle errors, ack/abandon

**Abstract methods (transport-specific):**
- Lifecycle: `ConnectAsync`, `EnsureTopologyAsync`, `EnsureOutboundTopologyAsync`, `StartConsumersAsync`, `StopConsumersAsync`, `DisposeAsync`
- Inbound: `GetMessageType`, `GetMessageBody`, `IsRequest`, `ExtractContext`, `SendReplyAsync`, `AcknowledgeAsync`, `AbandonAsync`
- Outbound: `IsStarted`, `PublishRequestAsync`, `PublishEventAsync`

**Design decisions:**
- `BinaryData` used for all payload passing between base and transport — zero-copy for ASB (native type), single allocation for others
- Reply serialization owned by base class — `SendReplyAsync` receives pre-serialized `BinaryData`, not `DispatchResult<object>`
- Error reply on request failure is standardized — all broker transports send an `InternalError` reply on handler failure, preventing caller timeouts
- HTTP and SignalR transports do not extend this base — they lack inbound dispatch, topology, and hosted service lifecycle
- **Configurable ResponseTimeout**: Each transport options class has a `ResponseTimeout` property (default 60s). Replaces any hardcoded timeout values.
- **No application retry**: Transports are one-shot dispatch + dead letter on failure. The `AbandonAsync` abstract method is called on event listener failure; transports decide how to dead-letter (RMQ/ASB use broker native redelivery). Application retry is the user's concern via `IPipelineBehavior<,>` + Polly.

## Amazon SQS (+SNS) Transport (`Conduit.Transport.AmazonSqs`)

`AmazonSqsTransport` extends `BrokerTransportBase<AmazonSqsInboundMessage>`. Uses AWS SDK (`AmazonSNSClient` + `AmazonSQSClient`).

**Registration:** `UseAmazonSqs(configure)` — no connection string (uses `Region` or `ServiceUri` for LocalStack). Registers singleton transport, `IHostedService` forwarding, non-keyed `IConduitTransport` (event broadcast), keyed `IConduitTransport` (endpoint routing).

**Topology:**
- Single SNS topic for all messages
- SQS queues per app, SNS subscriptions with filter policies route by type (`{"Type": ["FullTypeName"]}`)
- Commands/queries: shared subscription names (unicast). Events: per-app subscription names (multicast).
- Reply via direct SQS send (bypasses SNS)
- SQS queue name limit is 80 characters — `TopologyNaming` hash truncation activates frequently

**Wire format:** SQS/SNS message attributes: `Type`, `MessageId`, `CausationId`, `ConversationId`, `ReplyQueueUrl`, `CorrelationId`.

**Constraints:** Raw message delivery required on subscriptions (filter policies need it). Startup validates `RawMessageDelivery=true` and `VisibilityTimeoutSeconds >= ResponseTimeout`.

**Topology modes:** Production = standard. Developer/Test = tagged resources with background cleanup.

## ActiveMQ Artemis Transport (`Conduit.Transport.ActiveMQ`)

`ActiveMQTransport` extends `BrokerTransportBase<ActiveMQInboundMessage>`. Uses `AmqpNetLite` (AMQP 1.0).

**Registration:** `UseActiveMQ(connectionString, configure?)` — registers singleton transport, `IHostedService` forwarding, non-keyed `IConduitTransport` (event broadcast), keyed `IConduitTransport` (endpoint routing). Optional `Username`/`Password` for SASL PLAIN auth.

**Topology:**
- Two addresses: anycast for commands/queries, multicast for events
- Command/query queues: shared names on anycast address (unicast). Event queues: per-app names on multicast address (multicast).
- Reply queue: per-instance exclusive temporary queue (destroyed on shutdown)
- Topology managed via Artemis management address (`activemq.management`)

**Wire format:** AMQP 1.0 message properties: `Subject`=FullTypeName, `MessageId`, `CorrelationId`=ConversationId. Application properties: `CausationId`, `ReplyTo`, `ReplyCorrelationId`.

**Topology modes:** Production = durable. Developer = `auto-delete-delay` 4h. Test = `auto-delete-delay` 5min (native Artemis support via queue attributes).

**Concurrency:** AMQP link credit (prefetch count) per receiver link.

## Transactional Outbox (`Conduit.Outbox`)

The outbox guarantees at-least-once delivery for fire-and-forget messages (events and void routed commands). It solves the dual-write problem: business data and outbound messages are written in the same database transaction.

**Interception mechanism:** `IOutboundInterceptor` (in `Conduit.Middleware`, implemented by `OutboxCapture` when the outbox is registered) is offered each capture-eligible envelope by the generated mediator *before* local enqueue or transport send. `Intercept` returns `bool`: a successful capture (an ambient database transaction is active) suppresses both the local enqueue and the transport send for fire-and-forget events, with flush/relay delivering local listeners first and transports second once the transaction commits. Without an ambient transaction, capture is declined and the caller proceeds exactly as if no interceptor were registered, delivering right away, exactly as before the outbox existed. This is the symmetric counterpart to `IInboundInterceptor` (which intercepts messages arriving from transports).

**Pipeline behavior:** `OutboxBehavior<TRequest, TResult>` wraps the handler. After the handler returns, the behavior reads the captured envelopes not yet written in this scope from `OutboxCapture` and writes them to the outbox table via `IOutboxStore`, using the transaction from `IConduitConnectionFactory.GetCurrentTransaction()`. Because the behavior runs at the end of every pipeline in the scope — including the nested pipelines of events a handler publishes inline — each captured envelope is written exactly once, by the first pipeline to finish after it was captured, and every written envelope is added to the list the post-commit flush delivers. A faulted pipeline drops the unwritten envelopes it captured. Envelopes a host behavior captures after its `next()` returns — outside every `OutboxBehavior` — are written by `IOutboxPendingWriter.WritePendingAsync`, which runs the same write. If no transaction is active, the behavior passes through (no persistence).

**Relay:** `OutboxRelay` is a `BackgroundService` that polls the outbox table for pending entries older than a configurable grace period. It sends each entry via the appropriate transport (routing via `ConduitRuntime.ResolveRoute` for commands, broadcast for events) and marks entries as sent. Failed entries are retried up to `MaxRetries`, then marked as permanently failed. Uses `SKIP LOCKED` (or equivalent) for concurrent relay instances.

**Store abstraction:** `IOutboxStore` is the persistence interface. The built-in `DbConnectionOutboxStore` uses raw ADO.NET with `IOutboxDialect` for database-specific SQL. Four dialects ship: SQLite, SQL Server (`ROWLOCK + UPDLOCK + READPAST`, under a batch-level `SET TRANSACTION ISOLATION LEVEL READ COMMITTED`, because a pooled session may carry Snapshot or Serializable: measured on SQL Server 2022 with SqlClient 7.1.1), PostgreSQL (`FOR UPDATE SKIP LOCKED`), MySQL 8.0+ (`FOR UPDATE SKIP LOCKED`). Custom stores (Cosmos DB, MongoDB, etc.) implement `IOutboxStore` directly.

**Registration:** `conduit.AddOutbox(outbox => outbox.UseSqlite(...))` registers the capture, behavior, relay, store, and options in one call.

**Not captured:** Request/response messages (commands with results, queries) are excluded — the caller awaits a response and dispatch cannot be deferred. `[LocalOnly]` events never reach transports (broadcast is compiled out). A plain `[LocalOnly]` event is never offered to the interceptor; a `[FireAndForget, LocalOnly]` event is — it can be captured like any other fire-and-forget event, it just never gets a transport step on delivery.

## Shared Analysis Library (`Conduit.Analysis`)

`Conduit.Analysis` provides a shared type model (`DispatchableTypeInfo`, `DispatchableKind`) used by both scanning paths. The scanners themselves live in their consumers:

- **`RoslynScanner`** (in `Conduit.SourceGen`) — uses Roslyn symbol API (`INamedTypeSymbol`) at compile time. Walks `compilation.GlobalNamespace` to discover dispatchable types across the full compilation graph, including referenced assemblies. This is intentional for cross-assembly contract support (e.g., shared contract libraries referenced by handler projects). Types from NuGet packages may also be discovered; use `[RemoteContracts("Ns")]` to suppress CONDUIT001 for types without local handlers.
- **`ReflectionScanner`** (in `Conduit.TypeGen`) — uses Reflection metadata (`System.Reflection`) as an MSBuild task post-compilation. Works on the compiled assembly DLL.

Both scanners find types implementing `ICommand`, `ICommand<T>`, `IQuery<T>`, `IEvent` and produce `DispatchableTypeInfo` records.

## Incremental Build Support

`Conduit.TypeGen` MSBuild target specifies `Inputs` (the compiled assembly DLL) and `Outputs` (the generated `.ts` files). MSBuild skips the task when outputs are newer than inputs. This means TypeGen only re-runs when the assembly actually changes, not on every build.

`Conduit.SourceGen` uses Roslyn's incremental generator API (`IncrementalGeneratorInitializationContext`). Both dispatchable-type discovery AND handler/listener discovery use `CompilationProvider` with a `GlobalNamespace` walk (`RoslynScanner.Scan` / `RoslynScanner.ScanHandlers`), so handlers and listeners declared in **referenced assemblies** are discovered — not just those in the compilation under test. This lets one composition-root assembly compose handlers from many module assemblies (see `docs/modular-monolith.md`). The trade-off: the walk re-runs on any source change rather than caching per changed file. Referenced-assembly handlers must be accessible (public) to the generating assembly; an inaccessible one raises `CONDUIT004`. A handler/listener marked `[NotAutoRegistered]` is skipped (e.g. framework components wired via their own `Add*` extension, like `LocalAuditListener`).

## Fallback Behavior

**No `[DispatchableNamespace]` declared:** The source generator still generates dispatch code for any type annotated with `[Dispatchable]`. TypeGen produces a single `default.generated.ts` file containing all `[Dispatchable]` types. If neither attribute is used, no types are dispatchable and no TypeScript is generated.

## Authentication and Authorization

Authentication is handled by standard ASP.NET Core middleware (`AddAuthentication`, `UseAuthentication`) which protects both the HTTP dispatch endpoint and SignalR hub connection. Conduit does not add any authentication middleware — it uses whatever the host application has configured.

Authorization is **per-command/query** via attributes. Since all requests flow through a single dispatch endpoint, endpoint-level authorization is too coarse. Instead, each command and query declares its own requirements:

```csharp
[Authorize(Policy = "OrderAdmin")]
public record DeleteOrderCommand(Guid OrderId) : ICommand;

[Authorize] // requires authentication, no specific policy
public record CreateOrderCommand(string CustomerId) : ICommand<Guid>;

[AllowAnonymous] // explicitly public
public record GetProductCatalogQuery() : IQuery<ProductDto[]>;
```

### Compile-Time Safety

The source generator emits a diagnostic warning if a dispatchable command or query has neither `[Authorize]` nor `[AllowAnonymous]`. This prevents accidentally exposing an unauthenticated endpoint. The default behavior for unannotated commands is configurable:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.DefaultAuthPolicy = AuthPolicy.RequireAuthenticated; // default
    // or AuthPolicy.AllowAnonymous for apps without auth
});
```

### Authorization Behavior

The authorization behavior is consumer-defined but follows a standard pattern using ASP.NET Core's `IAuthorizationService`:

```csharp
public class AuthorizationBehavior<TRequest, TResult>(
    IHttpContextAccessor httpContextAccessor,
    IAuthorizationService authorizationService) : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        if (typeof(TRequest).GetCustomAttribute<AllowAnonymousAttribute>() is not null)
        {
            return await next();
        }

        var user = httpContextAccessor.HttpContext?.User;

        if (user?.Identity?.IsAuthenticated != true)
        {
            throw new UnauthorizedException("Authentication required");
        }

        var authorizeAttribute = typeof(TRequest).GetCustomAttribute<AuthorizeAttribute>();
        if (authorizeAttribute?.Policy is { } policy)
        {
            var result = await authorizationService.AuthorizeAsync(user, policy);
            if (!result.Succeeded)
            {
                throw new UnauthorizedException($"Policy '{policy}' not satisfied");
            }
        }

        return await next();
    }
}
```

### What Conduit Provides vs Consumer

| Concern | Provided by |
|---------|------------|
| `[Authorize]`, `[AllowAnonymous]` attributes | Reuse ASP.NET Core's existing attributes |
| Compile-time diagnostic for missing auth attribute | Conduit.SourceGen |
| `DefaultAuthPolicy` configuration | Conduit |
| `AuthorizationBehavior` implementation | Consumer |
| Policy definitions | Consumer (`builder.Services.AddAuthorization(...)`) |

## Future Considerations

The `BrokerTransportBase<TMessage>` pattern makes adding new broker transports straightforward — implement ~15 abstract methods for broker-specific primitives. The transport test matrix (`docs/transport-test-matrices.md`) defines the full test suite required for any new transport.

Potential future transports (not planned):
- `Conduit.Transport.Rebus` — Rebus adapter wrapping its bus abstraction as `IConduitTransport`
- `Conduit.Transport.NATS` — NATS JetStream transport
- `Conduit.Transport.Redis` — Redis Streams transport
