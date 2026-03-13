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

- Message broker integration (Rebus will be added in a future phase)
- Built-in pipeline behaviors (validation, auth, logging, OTel) — consumers compose their own
- npm package for Angular — TypeScript codegen produces everything the frontend needs
- Streaming/subscription queries — SignalR streaming is a separate concern outside Conduit

## Project Structure

```
Conduit/
├── src/
│   ├── Conduit/                        # Core interfaces and types
│   ├── Conduit.Analysis/               # Shared Roslyn analysis for SourceGen and TypeGen
│   ├── Conduit.SourceGen/              # Roslyn incremental source generator
│   ├── Conduit.AspNetCore/             # HTTP dispatch endpoint + SignalR hub + event transport
│   └── Conduit.TypeGen/                # MSBuild task for TypeScript emission
├── tests/
│   ├── Conduit.Tests/
│   ├── Conduit.Analysis.Tests/
│   ├── Conduit.SourceGen.Tests/
│   ├── Conduit.AspNetCore.Tests/
│   └── Conduit.TypeGen.Tests/
├── samples/
│   └── Conduit.Sample/
└── Conduit.sln
```

### Dependency Graph

```
Conduit.SourceGen ──→ Conduit.Analysis ──→ (Roslyn APIs)
Conduit.TypeGen   ──→ Conduit.Analysis ──→ (Roslyn APIs)
Conduit.AspNetCore ──→ Conduit ──→ MS.Extensions.DI.Abstractions
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

Separate from `IPipelineBehavior<TRequest, TResult>` because events have no result type and use continue-on-failure semantics. Each event listener gets its own behavior pipeline, wrapped in `SafeExecute`:

```csharp
// Generated — each listener gets its own pipeline, isolated by SafeExecute
case OrderCreatedEvent e:
    await SafeExecute(async () =>
    {
        var behavior = serviceProvider.GetRequiredService<EventLoggingBehavior<OrderCreatedEvent>>();
        var listener = serviceProvider.GetRequiredService<SendConfirmationListener>();
        await behavior.HandleAsync(e,
            () => listener.HandleAsync(e, cancellationToken),
            cancellationToken);
    });
    await SafeExecute(async () =>
    {
        var behavior = serviceProvider.GetRequiredService<EventLoggingBehavior<OrderCreatedEvent>>();
        var listener = serviceProvider.GetRequiredService<UpdateInventoryListener>();
        await behavior.HandleAsync(e,
            () => listener.HandleAsync(e, cancellationToken),
            cancellationToken);
    });
    break;
```

A behavior failure within one listener's pipeline does not affect other listeners.

Event behaviors are registered alongside command/query behaviors:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddBehavior<LoggingBehavior>();           // commands/queries
    conduit.AddEventBehavior<EventLoggingBehavior>(); // events
});
```

### Event Transport

```csharp
public interface IEventTransport
{
    Task DeliverAsync(IEvent @event, CancellationToken cancellationToken);
}
```

When `PublishAsync` is called, the mediator:
1. Runs all in-process `IEventListener<T>` implementations (continue on failure)
2. Delivers to all registered `IEventTransport` implementations

### Error Model

Exceptions flow naturally through .NET. The dispatch endpoint (HTTP/SignalR) is the single mapping point to a structured envelope for the wire.

```csharp
public abstract class ConduitException(string code, int statusCode, string message)
    : Exception(message)
{
    public string Code => code;
    public int StatusCode => statusCode;
}

// Built-in subclasses
public class ValidationException(string message)
    : ConduitException("Validation", 422, message);

public class NotFoundException(string message)
    : ConduitException("NotFound", 404, message);

public class UnauthorizedException(string message)
    : ConduitException("Unauthorized", 401, message);

public class ConflictException(string message)
    : ConduitException("Conflict", 409, message);
```

Consumers extend `ConduitException` for domain-specific errors:

```csharp
public class InsufficientCreditException(string message)
    : ConduitException("InsufficientCredit", 422, message);
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
        var handler = serviceProvider.GetRequiredService<CreateOrderHandler>();
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
    var handler = serviceProvider.GetRequiredService<CreateOrderHandler>();

    return await behavior1.HandleAsync(command,
        () => behavior2.HandleAsync(command,
            () => handler.HandleAsync(command, cancellationToken),
            cancellationToken),
        cancellationToken);
}
```

### 3. DI Registration

```csharp
public static IServiceCollection AddConduitHandlers(this IServiceCollection services)
{
    services.AddTransient<CreateOrderHandler>();
    services.AddTransient<DeleteOrderHandler>();
    services.AddTransient<OrderCreatedListener>();
    return services;
}
```

### 4. Mediator Implementation

Generated with strongly-typed overloads to avoid boxing in application code:

```csharp
internal sealed class ConduitMediator(
    IServiceProvider serviceProvider,
    IEnumerable<IEventTransport> transports,
    ILogger<ConduitMediator> logger) : IMediator
{
    // Strongly-typed overloads — compiler picks these, zero boxing
    public Task<Guid> ExecuteAsync(
        CreateOrderCommand command,
        CancellationToken cancellationToken)
        => ConduitDispatch.Execute(command, serviceProvider, cancellationToken);

    public Task ExecuteAsync(
        DeleteOrderCommand command,
        CancellationToken cancellationToken)
        => ConduitDispatch.Execute(command, serviceProvider, cancellationToken);

    // Generic interface fallback — only used by dispatch endpoint
    Task<TResult> IMediator.ExecuteAsync<TResult>(
        ICommand<TResult> command,
        CancellationToken cancellationToken)
        => command switch
        {
            CreateOrderCommand cmd =>
                Unsafe.As<Task<TResult>>(ExecuteAsync(cmd, cancellationToken)),
            _ => throw new InvalidOperationException($"No handler for {command.GetType().Name}")
        };

    // Void command interface fallback
    Task IMediator.ExecuteAsync(
        ICommand command,
        CancellationToken cancellationToken)
        => command switch
        {
            DeleteOrderCommand cmd => ExecuteAsync(cmd, cancellationToken),
            _ => throw new InvalidOperationException($"No handler for {command.GetType().Name}")
        };

    // Events — continue on failure
    public async Task PublishAsync(IEvent @event, CancellationToken cancellationToken)
    {
        switch (@event)
        {
            case OrderCreatedEvent e:
                await SafeExecute(
                    () => serviceProvider.GetRequiredService<SendConfirmationListener>()
                        .HandleAsync(e, cancellationToken));
                await SafeExecute(
                    () => serviceProvider.GetRequiredService<UpdateInventoryListener>()
                        .HandleAsync(e, cancellationToken));
                break;
        }

        foreach (var transport in transports)
        {
            await SafeExecute(() => transport.DeliverAsync(@event, cancellationToken));
        }
    }

    // Wire dispatch — returns object, boxing is irrelevant (serialization boundary)
    // Void commands return null; the dispatch endpoint wraps in DispatchResult<object> { Data = null }
    public static async Task<object?> Dispatch(
        string type,
        JsonElement payload,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
        => type switch
        {
            "CreateOrder" => await ConduitDispatch.Execute(
                JsonSerializer.Deserialize<CreateOrderCommand>(payload)!,
                serviceProvider,
                cancellationToken),
            "DeleteOrder" => await ConduitDispatch.ExecuteVoid(
                JsonSerializer.Deserialize<DeleteOrderCommand>(payload)!,
                serviceProvider,
                cancellationToken),
            _ => throw new InvalidOperationException($"Unknown command: {type}")
        };

    private async Task SafeExecute(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Event listener or transport failed");
        }
    }
}
```

### Compile-Time Diagnostics

The generator emits diagnostics for:
- Command or query with no matching handler
- Multiple handlers for the same command or query
- `[Dispatchable]` on a type that doesn't implement a marker interface

## ASP.NET Core Integration (`Conduit.AspNetCore`)

### HTTP Dispatch Endpoint

```csharp
public static class ConduitEndpoints
{
    public static IEndpointRouteBuilder MapConduit(
        this IEndpointRouteBuilder endpoints,
        string path = "/api/dispatch")
    {
        endpoints.MapPost(path, async (
            DispatchEnvelope envelope,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await ConduitMediator.Dispatch(
                    envelope.Type, envelope.Payload,
                    serviceProvider, cancellationToken);
                return Results.Ok(new DispatchResult<object> { Data = result });
            }
            catch (ConduitException ex)
            {
                return Results.Json(
                    new DispatchResult<object>
                    {
                        Error = new DispatchError(ex.Code, ex.Message)
                    },
                    statusCode: ex.StatusCode);
            }
            catch (Exception ex)
            {
                // Log, OTel
                return Results.Json(
                    new DispatchResult<object>
                    {
                        Error = new DispatchError("InternalError", "An unexpected error occurred")
                    },
                    statusCode: 500);
            }
        });

        return endpoints;
    }
}
```

### SignalR Hub

Dual purpose — handles client-to-server dispatch and server-to-client event delivery on the same connection:

```csharp
public class ConduitHub : Hub
{
    public async Task<DispatchResult<object>> Dispatch(
        string type,
        JsonElement payload)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            var result = await ConduitMediator.Dispatch(
                type, payload,
                Context.GetHttpContext()!.RequestServices,
                cancellationToken);
            return new DispatchResult<object> { Data = result };
        }
        catch (ConduitException ex)
        {
            return new DispatchResult<object>
            {
                Error = new DispatchError(ex.Code, ex.Message)
            };
        }
        catch (Exception ex)
        {
            // Log, OTel
            return new DispatchResult<object>
            {
                Error = new DispatchError("InternalError", "An unexpected error occurred")
            };
        }
    }
}
```

### SignalR Event Transport

```csharp
public class SignalREventTransport(IHubContext<ConduitHub> hub) : IEventTransport
{
    public Task DeliverAsync(IEvent @event, CancellationToken cancellationToken)
    {
        var eventName = @event.GetType().Name;
        return hub.Clients.All.SendAsync(eventName, @event, cancellationToken);
    }
}
```

### Registration

```csharp
public static class ConduitAspNetCoreExtensions
{
    public static IServiceCollection AddConduit(this IServiceCollection services)
    {
        services.AddConduitHandlers();  // generated
        services.AddSingleton<IMediator, ConduitMediator>();  // generated
        services.AddSignalR();
        services.AddSingleton<IEventTransport, SignalREventTransport>();
        return services;
    }
}

// Program.cs
builder.Services.AddConduit();
// ...
app.MapConduit();
app.MapHub<ConduitHub>("/hub");
```

## TypeScript Codegen (`Conduit.TypeGen`)

MSBuild task that runs on `dotnet build`. Uses `Conduit.Analysis` to scan the compiled assembly.

### Configuration

```xml
<PropertyGroup>
  <ConduitTypeGenEnabled>true</ConduitTypeGenEnabled>
  <ConduitTypeGenOutput>../frontend/src/generated</ConduitTypeGenOutput>
</PropertyGroup>
```

### Namespace Segmentation

Driven by `[DispatchableNamespace]` assembly attributes:

```csharp
[assembly: DispatchableNamespace("Erp.Orders")]      // → erp-orders.generated.ts
[assembly: DispatchableNamespace("Erp.Inventory")]    // → erp-inventory.generated.ts
```

### Output Structure

```
frontend/src/generated/
├── conduit.ts                    # DispatchResult, DispatchError, ConduitError, abstract transport
├── erp-orders.generated.ts       # Interfaces + type maps for Erp.Orders namespace
├── erp-inventory.generated.ts    # Interfaces + type maps for Erp.Inventory namespace
└── conduit.generated.ts          # Merge file: composed type maps + typed Mediator class
```

### Generated Types Per Namespace

```typescript
// erp-orders.generated.ts

export interface CreateOrderCommand {
  customerId: string;
  items: OrderLineItem[];
}

export interface OrderLineItem {
  productId: string;
  quantity: number;
}

export interface CreateOrderResult {
  orderId: string;
}

export interface GetOrderQuery {
  orderId: string;
}

export interface OrderDto {
  id: string;
  customerId: string;
  status: OrderStatus;
}

export type OrderStatus = 'Pending' | 'Confirmed' | 'Shipped';

export interface OrderCreatedEvent {
  orderId: string;
  customerId: string;
}

export interface DeleteOrderCommand {
  orderId: string;
}

export interface CommandMap {
  'CreateOrder': { request: CreateOrderCommand; response: CreateOrderResult };
}

export interface VoidCommandMap {
  'DeleteOrder': { request: DeleteOrderCommand };
}

export interface QueryMap {
  'GetOrder': { request: GetOrderQuery; response: OrderDto };
}

export interface EventMap {
  'OrderCreated': OrderCreatedEvent;
}
```

### Base TypeScript (conduit.ts)

Contains envelope types, error class, and abstract transport base. This file has no dependency on generated type maps — it is always valid regardless of what commands/queries exist.

```typescript
// conduit.ts

export interface DispatchResult<T> {
  data?: T;
  error?: DispatchError;
}

export interface DispatchError {
  code: string;
  message: string;
  details?: DispatchErrorDetail[];
}

export interface DispatchErrorDetail {
  field?: string;
  message: string;
}

export class ConduitError extends Error {
  constructor(public readonly error: DispatchError) {
    super(error.message);
  }
}

export interface Subscription {
  unsubscribe(): void;
}

export abstract class ConduitTransport {
  abstract dispatch(type: string, request: unknown): Promise<DispatchResult<unknown>>;
  abstract subscribe(event: string, handler: (data: unknown) => void): Subscription;
}
```

### Merge File (conduit.generated.ts)

Composes all namespace type maps and provides the typed `Mediator` class that references them. This file imports from `conduit.ts` (base types) and the namespace files (type maps) — no circular dependency.

```typescript
// conduit.generated.ts
import { ConduitTransport, ConduitError, DispatchResult, Subscription } from './conduit';
import { CommandMap as OrdersCommands, VoidCommandMap as OrdersVoidCommands,
         QueryMap as OrdersQueries, EventMap as OrdersEvents } from './erp-orders.generated';
import { CommandMap as InventoryCommands, VoidCommandMap as InventoryVoidCommands,
         QueryMap as InventoryQueries, EventMap as InventoryEvents } from './erp-inventory.generated';

export type AllCommands = OrdersCommands & InventoryCommands;
export type AllVoidCommands = OrdersVoidCommands & InventoryVoidCommands;
export type AllQueries = OrdersQueries & InventoryQueries;
export type AllEvents = OrdersEvents & InventoryEvents;

export abstract class Mediator {
  constructor(protected transport: ConduitTransport) {}

  async execute<K extends keyof AllCommands>(
    type: K,
    request: AllCommands[K]['request']
  ): Promise<AllCommands[K]['response']> {
    const result = await this.transport.dispatch(type, request) as DispatchResult<AllCommands[K]['response']>;
    if (result.error) {
      throw new ConduitError(result.error);
    }
    return result.data!;
  }

  async executeVoid<K extends keyof AllVoidCommands>(
    type: K,
    request: AllVoidCommands[K]['request']
  ): Promise<void> {
    const result = await this.transport.dispatch(type, request);
    if (result.error) {
      throw new ConduitError(result.error);
    }
  }

  async fetch<K extends keyof AllQueries>(
    type: K,
    request: AllQueries[K]['request']
  ): Promise<AllQueries[K]['response']> {
    const result = await this.transport.dispatch(type, request) as DispatchResult<AllQueries[K]['response']>;
    if (result.error) {
      throw new ConduitError(result.error);
    }
    return result.data!;
  }

  on<K extends keyof AllEvents>(
    event: K,
    handler: (data: AllEvents[K]) => void
  ): Subscription {
    return this.transport.subscribe(event as string, handler as (data: unknown) => void);
  }
}
```

The consumer extends `Mediator` and provides a `ConduitTransport` implementation:

```typescript
// App-specific: SignalR transport
@Injectable({ providedIn: 'root' })
export class SignalRTransport extends ConduitTransport {
  private hub: HubConnection;

  constructor() {
    super();
    this.hub = new HubConnectionBuilder()
      .withUrl('/hub')
      .withAutomaticReconnect()
      .build();
    this.hub.start();
  }

  dispatch(type: string, request: unknown): Promise<DispatchResult<unknown>> {
    return this.hub.invoke('Dispatch', type, request);
  }

  subscribe(event: string, handler: (data: unknown) => void): Subscription {
    this.hub.on(event, handler);
    return { unsubscribe: () => this.hub.off(event, handler) };
  }
}

// App-specific: Mediator
@Injectable({ providedIn: 'root' })
export class AppMediator extends Mediator {
  constructor(transport: SignalRTransport) {
    super(transport);
  }
}
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

Unified pub/sub across transports:

```
Publisher calls mediator.PublishAsync(new OrderCreatedEvent(...))
    │
    ├─ In-process: IEventListener<OrderCreatedEvent> handlers (continue on failure)
    ├─ SignalR: SignalREventTransport → hub.Clients.All.SendAsync("OrderCreatedEvent", ...)
    └─ Rebus (future): RebusEventTransport → bus.Publish(...)
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
- Event pipeline behaviors woven per-listener inside SafeExecute
- Void and result commands generate correct overloads
- Missing handler produces compile-time diagnostic
- Missing `[Authorize]`/`[AllowAnonymous]` on dispatchable type produces diagnostic

**Conduit.AspNetCore.Tests:**
- Integration tests with `WebApplicationFactory`
- HTTP POST dispatch returns correct envelope for success, `ConduitException`, unexpected exception
- SignalR dispatch returns correct envelope
- `SignalREventTransport` delivers events to connected clients

**Conduit.TypeGen.Tests:**
- C# type → TypeScript mapping for all supported types
- Namespace segmentation produces correct files
- Merge file composition
- Generated TypeScript compiles (`tsc --noEmit`)

## Shared Analysis Library (`Conduit.Analysis`)

`Conduit.Analysis` provides a unified API for scanning types, but has two internal paths:

- **Roslyn symbol API** (`INamedTypeSymbol`) — used by `Conduit.SourceGen` at compile time. Works on source code during compilation.
- **Reflection metadata** (`System.Reflection`) — used by `Conduit.TypeGen` as an MSBuild task post-compilation. Works on the compiled assembly DLL.

Both paths use the same resolution logic: find types implementing `ICommand`, `ICommand<T>`, `IQuery<T>`, `IEvent`; apply `[DispatchableNamespace]`/`[Dispatchable]`/`[NotDispatchable]` precedence; group by namespace.

The public API returns a common model (`DispatchableTypeInfo`, `NamespaceGroup`) that both consumers use.

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

- `Conduit.Rebus` package implementing `IEventTransport`
- Commands can be dispatched to other services via Rebus (outbox pattern)
- Events published in-process are also published to message broker
- `IEvent` marker interface is the natural boundary for distributed events

The `IEventTransport` abstraction means adding Rebus is one new class + service registration.
