# Conduit — Mediator Library Design

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
│   ├── Conduit/                        # Core interfaces and types
│   ├── Conduit.Analysis/               # Shared type model (DispatchableTypeInfo, etc.)
│   ├── Conduit.SourceGen/              # Roslyn source generator + RoslynScanner
│   ├── Conduit.Transport.Http/   # HTTP dispatch endpoint + HTTP transport
│   ├── Conduit.Transport.SignalR/      # SignalR hub + SignalR transport
│   └── Conduit.TypeGen/                # MSBuild task for TypeScript emission + ReflectionScanner
├── tests/
│   ├── Conduit.Tests/
│   ├── Conduit.Analysis.Tests/
│   ├── Conduit.SourceGen.Tests/
│   ├── Conduit.Transport.Http.Tests/
│   └── Conduit.TypeGen.Tests/
├── samples/
│   └── Conduit.Sample/
└── Conduit.slnx
```

### Dependency Graph

```
Conduit.SourceGen ──→ Conduit.Analysis ──→ (Roslyn APIs)
Conduit.TypeGen   ──→ Conduit.Analysis ──→ (Roslyn APIs)
Conduit.Transport.Http ──→ Conduit ──→ MS.Extensions.DI.Abstractions
Conduit.Transport.SignalR    ──→ Conduit ──→ MS.Extensions.DI.Abstractions

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
    conduit.AddBehavior<ValidationBehavior>(); // runs third (closest to handler)
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

A listener failure does not prevent other listeners from running. `PublishAsync` (local path) catches `EventListenerException` (fire-and-forget). `DispatchEventAsync` (wire path) lets it propagate so transports can retry/dead-letter.

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

Extended mediator interface for inbound wire dispatch. Transport packages (HTTP, SignalR) inject `IWireMediator`; application code uses `IMediator`. `HandleWireRequestAsync` chains registered `IWireInterceptor` instances, deserializes the payload, dispatches to the handler, and wraps the result in `DispatchResult<T>`. `HandleWireEventAsync` does the same for events — lets `EventListenerException` propagate to the transport for retry/dead-letter, swallows other exceptions.

### Message Context

`MessageContext` tracks causation through dispatch chains via `AsyncLocal`. Every dispatch gets a unique `MessageId`. Nested dispatches inherit `ConversationId` (the root message's ID) and set `CausationId` to the parent's `MessageId`.

```
CreateOrderCommand    (MessageId=A, CausationId=null, ConversationId=A)
  ├─ OrderCreatedEvent  (MessageId=B, CausationId=A, ConversationId=A)
  └─ AuditLogEvent      (MessageId=C, CausationId=A, ConversationId=A)
       └─ NotifyCommand  (MessageId=D, CausationId=C, ConversationId=A)
```

Two factory methods:
- `MessageContext.StartDispatch()` — local dispatch, generates IDs, inherits from parent if nested
- `MessageContext.StartDispatchFromTransport(messageId, causationId, conversationId)` — inbound wire dispatch, all values from broker message properties

Access via `MessageContext.Current` (static property). Set automatically by the generated mediator and `ConduitRuntime` wire dispatch. Context is set before the `IWireInterceptor` chain runs.

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
    public TopologyMode TopologyMode { get; set; } = TopologyMode.Production;
    public string? DeveloperPrefix { get; set; }
}
```

`ConduitRuntime.ResolveRoute` checks the route table before local dispatch. If a message type matches a route, the generated mediator resolves the transport via `ConduitRuntime.ResolveTransport(serviceProvider, endpoint)` (keyed DI lookup), serializes the request into a `DispatchEnvelope`, and sends it via the transport. If no route matches, dispatch is local.

Configuration uses a fluent builder API (runtime only — no attribute-based routing):

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("inventory", endpoint =>
        endpoint.UseSignalR(new Uri(config["InventoryServiceUrl"])));

    conduit.Route("Inventory.Commands").To("inventory");
});
```

### Event Broadcast

Events broadcast — they don't route. `PublishAsync` dispatches to local listeners then broadcasts to all DI-registered `IConduitTransport` instances. Events marked `[LocalOnly]` skip the broadcast step entirely (compile-time, zero runtime cost).

```csharp
[LocalOnly]
public record UserPasswordChangedEvent(Guid UserId) : IEvent;
```

Inbound filtering is handled by `TypeFilterInterceptor` (rejects unauthorized types arriving from transports). `[LocalOnly]` is the outbound complement — preventing events from leaving.

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
| `Developer` | `{DeveloperPrefix}` (default: `Environment.UserName`) | Non-durable | 4 hours |
| `Test` | `test-{random}` | Non-durable | 5 minutes |

Developer mode gives each developer isolated broker resources (e.g. `bpage-conduit` instead of `conduit`). Test mode creates fully isolated, short-lived resources for integration tests that self-clean.

Configuration:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.TopologyMode = TopologyMode.Developer;
    conduit.DeveloperPrefix = "myprefix";  // optional, defaults to Environment.UserName
    conduit.SystemName = "myapp";          // optional, defaults to "conduit"
});
```

### Topic-Based Message Routing (Broker Transports)

All broker transports (Azure Service Bus, RabbitMQ) use a unified topic-based routing model. All message types — commands, queries, and events — publish to a single shared topic/exchange. Subscriptions and queue bindings route messages from the topic to the appropriate app queue. Reply messages are the only exception; they bypass the topic and go point-to-point.

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

Unicast correctness for commands/queries in RabbitMQ relies on only one app binding a given type's routing key. If two apps bind the same command type, RabbitMQ will round-robin — which is incorrect. ASB enforces unicast via shared subscription names.

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

The generator skips CONDUIT001 for types whose fully-qualified name starts with a declared `[RemoteContracts]` pattern. Types outside declared patterns still produce the warning if they lack a handler.

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
    command.Validate();
    var behavior1 = serviceProvider.GetRequiredService<LoggingBehavior<CreateOrderCommand, Guid>>();
    var behavior2 = serviceProvider.GetRequiredService<AuthBehavior<CreateOrderCommand, Guid>>();
    var handler = serviceProvider.GetRequiredService<ICommandHandler<CreateOrderCommand, Guid>>();

    return await behavior1.HandleAsync(command,
        () => behavior2.HandleAsync(command,
            () => handler.HandleAsync(command, cancellationToken),
            cancellationToken),
        cancellationToken);
}
```

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
- **Wire dispatch** — `HandleWireRequestAsync`/`HandleWireEventAsync` chain `IWireInterceptor` instances, deserialize by type name, and dispatch locally
- **Pipeline behaviors** — resolved via `ConduitRuntime.ResolvePipelines` in registration order (first = outermost)

### 5. REST Endpoint Mappings (conditional)

When the compilation references `Conduit.Transport.Http`, the generator emits `ConduitRestEndpoints` with `MapConduitGet<T>`, `MapConduitPost<T>`, etc. — compile-time type switches that delegate to `ConduitRestHandlers`.

### Compile-Time Diagnostics

The generator emits diagnostics for:
- `CONDUIT001` — Command or query with no matching handler (suppressed by `[assembly: RemoteContracts("Ns")]` for remote contract types)
- `CONDUIT002` — Multiple handlers for the same command or query
- `CONDUIT004` — Missing `[Authorize]` or `[AllowAnonymous]` on a dispatchable type

## HTTP Integration (`Conduit.Transport.Http`)

### Envelope Dispatch Endpoint

`MapConduitEndpoint()` maps two POST endpoints for Conduit-aware consumers (service-to-service, TypeScript mediator):
- `POST /api/dispatch` — commands and queries (returns `DispatchResult<object>`)
- `POST /api/dispatch/publish` — events (fire-and-forget, returns 200 OK)

Delegates to `IWireMediator.HandleWireRequestAsync` / `HandleWireEventAsync`. Error codes map to HTTP status codes via `MapStatusCode()`. Returns `DispatchResult` envelope format.

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

Sends commands and queries to a remote Conduit endpoint via HTTP POST:

```csharp
public class HttpTransport(HttpClient httpClient, string path = "/api/dispatch") : IConduitTransport
{
    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => throw new NotSupportedException("HTTP transport does not support fire-and-forget events.");

    public async Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(path, envelope, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<DispatchResult<JsonElement?>>(cancellationToken);
        if (result?.Error is not null)
            throw new InvalidOperationException($"Remote dispatch failed: [{result.Error.Code}] {result.Error.Message}");
        return result?.Data;
    }
}
```

### Registration

```csharp
// Program.cs
builder.Services.AddConduit(conduit =>       // generated — registers handlers, mediator, core infra
{
    conduit.AddEndpoint("inventory", ep =>
        ep.UseSignalR(new Uri(config["InventoryUrl"])));
    conduit.Route("Inventory.Commands").To("inventory");
    conduit.AddPipeline(typeof(LoggingBehavior<,>));
});
builder.Services.AddConduitSignalR();        // registers SignalR hub + server-mode transport

app.MapConduitEndpoint();                    // envelope dispatch for Conduit-aware consumers
app.MapHub<ConduitHub>("/hub");              // SignalR hub

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
import { HubConnectionBuilder } from '@microsoft/signalr';
import { ConduitClient, SignalRTransport } from '../generated/conduit.generated';

const connection = new HubConnectionBuilder().withUrl('/hub').build();
const conduit = new ConduitClient(new SignalRTransport(connection));
await connection.start();

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

1. **Command `Validate()` method** — precondition guards. Fast fail on malformed requests. These are developer mistakes, not user errors. Single throw on first failure.

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

**Framework:** MSTest + AwesomeAssertions. NSubstitute as needed.

### Test Coverage by Project

**Conduit.Tests:**
- `ConduitException` subclasses produce correct codes and status codes
- `Validate()` throws on invalid commands, no-ops on valid

**Conduit.Analysis.Tests:**
- Scanner identifies dispatchable types from compiled assemblies
- `[DispatchableNamespace]`, `[Dispatchable]`, `[NotDispatchable]` precedence rules
- Namespace grouping logic

**Conduit.SourceGen.Tests:**
- Generated dispatch routes to correct handler
- Pipeline behaviors woven in correct order
- Event pipeline behaviors woven per-listener with inline try/catch
- Void and result commands generate correct overloads
- Missing handler produces compile-time diagnostic
- Missing `[Authorize]`/`[AllowAnonymous]` on dispatchable type produces diagnostic

**Conduit.Transport.Http.Tests:**
- Integration tests with full HTTP + SignalR pipeline
- HTTP POST dispatch returns correct envelope for success, `ConduitException`, unexpected exception
- SignalR dispatch returns correct envelope
- `SignalRTransport` delivers events to connected clients

**Conduit.TypeGen.Tests:**
- C# type → TypeScript mapping for all supported types
- Namespace segmentation produces correct files
- Merge file composition
- Generated TypeScript compiles (`tsc --noEmit`)

## Shared Analysis Library (`Conduit.Analysis`)

`Conduit.Analysis` provides a shared type model (`DispatchableTypeInfo`, `DispatchableKind`, `NamespaceGroup`) and resolution logic used by both scanning paths. The scanners themselves live in their consumers:

- **`RoslynScanner`** (in `Conduit.SourceGen`) — uses Roslyn symbol API (`INamedTypeSymbol`) at compile time. Works on source code during compilation.
- **`ReflectionScanner`** (in `Conduit.TypeGen`) — uses Reflection metadata (`System.Reflection`) as an MSBuild task post-compilation. Works on the compiled assembly DLL.

Both scanners use the same resolution logic from `Conduit.Analysis`: find types implementing `ICommand`, `ICommand<T>`, `IQuery<T>`, `IEvent`; apply `[DispatchableNamespace]`/`[Dispatchable]`/`[NotDispatchable]` precedence; group by namespace.

## Incremental Build Support

`Conduit.TypeGen` MSBuild target specifies `Inputs` (the compiled assembly DLL) and `Outputs` (the generated `.ts` files). MSBuild skips the task when outputs are newer than inputs. This means TypeGen only re-runs when the assembly actually changes, not on every build.

`Conduit.SourceGen` uses Roslyn's incremental generator API (`IncrementalGeneratorInitializationContext`) which caches automatically based on syntax/symbol changes.

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

## Future: Rebus Integration

Not built now, but the design accommodates it:

- `Conduit.Rebus` package implementing `IConduitTransport`
- Commands can be dispatched to other services via Rebus (outbox pattern)
- Events published in-process are also published to message broker
- `IEvent` marker interface is the natural boundary for distributed events

The `IConduitTransport` abstraction means adding Rebus is one new class + endpoint registration:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("message-bus", ep => ep.UseRebus(rebusConfig));
    conduit.Route("Orders.Events").To("message-bus");
});
```
