# Configuration Reference

## ConduitBuilder Methods

Available inside the `AddConduit` callback:

```csharp
builder.Services.AddConduit(conduit =>
{
    // methods below
});
```

| Method | Purpose | Example |
|---|---|---|
| `AddEndpoint(name, configure)` | Register a named remote endpoint with a transport | `conduit.AddEndpoint("orders-api", e => e.UseHttp("https://localhost:5002"))` |
| `Route(pattern)` | Route commands/queries by namespace prefix to an endpoint | `conduit.Route("Orders.Commands").To("orders-api")` |
| `Route<T>()` | Route commands/queries using a type's namespace as the pattern | `conduit.Route<CreateOrder>().To("orders-api")` |
| `AddPipeline(behaviorType)` | Register an open-generic `IPipelineBehavior<,>`. First registered = outermost. Wraps commands, queries, and events uniformly. | `conduit.AddPipeline(typeof(LoggingBehavior<,>))` |
| `AddTypeFilter(configure)` | Add a wire interceptor that restricts which types external clients may dispatch | `conduit.AddTypeFilter(f => f.AllowType<CreateOrder>())` |

## Attributes

| Attribute | Target | Purpose |
|---|---|---|
| `[LocalOnly]` | `class` (events) | Prevent event from being broadcast to transports. Dispatches only to local listeners. |
| `[ClientVisible]` | `class` (any message or DTO) | Include type in TypeScript code generation output. No runtime effect. |
| `[assembly: RemoteContracts("Ns")]` | `assembly` | Suppress CONDUIT001 for types whose namespace starts with the given pattern. Use when referencing shared contracts handled by another service. |

## MSBuild Properties (TypeGen)

Set in your project's `.csproj` to control TypeScript generation.

| Property | Default | Purpose |
|---|---|---|
| `ConduitTypeGenEnabled` | `false` | Set to `true` to enable TypeScript generation on build |
| `ConduitTypeGenOutput` | `ClientApp/generated` | Output directory for generated `.ts` files |
| `ConduitTransports` | `Http;SignalR` | Semicolon-separated list of transports to emit in `conduit.ts` |

## Diagnostics

| Code | Severity | Description | Resolution |
|---|---|---|---|
| CONDUIT001 | Warning | No handler found for a command or query | Add a handler class implementing `ICommandHandler<T>` / `IQueryHandler<T, TResult>`, or add `[assembly: RemoteContracts("Namespace")]` if the type is handled by another service |
| CONDUIT002 | Error | Multiple handlers found for a command or query | Remove the duplicate handler. Only one handler per command/query is allowed. |
| CONDUIT004 | Warning | Dispatchable type has neither `[Authorize]` nor `[AllowAnonymous]` | Add `[Authorize]` or `[AllowAnonymous]` to the type |

Next: [FAQ](faq.md)
