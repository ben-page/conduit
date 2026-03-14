# Conduit

Source-generated mediator library for .NET 10 with HTTP/SignalR transport, TypeScript codegen, and unified event transport. Replaces MediatR with a reflection-free, compile-time approach.

## Architecture

Six source projects, six test projects, one shared test utility, two sample apps:

```
src/Conduit/                        Core interfaces, routing, dispatch pipeline, configuration — net10.0
src/Conduit.Analysis/               Shared type model (DispatchableTypeInfo, etc.) — netstandard2.0 (PolySharp)
src/Conduit.SourceGen/              Roslyn incremental source generator + RoslynScanner — netstandard2.0 (PolySharp)
src/Conduit.TypeGen/                MSBuild task for TypeScript emission + ReflectionScanner — net10.0

src/Conduit.Transport.Http/         HTTP transport (outbound HttpTransport + inbound MapConduitEndpoint) — net10.0
src/Conduit.Transport.SignalR/      SignalR transport (outbound SignalRTransport + inbound ConduitHub) — net10.0
```

**Dependency graph:**
- `Transport.Http` → `Conduit` (core) + ASP.NET Core
- `Transport.SignalR` → `Conduit` (core) + ASP.NET Core + SignalR
- `SourceGen` → `Analysis` → Roslyn APIs
- `TypeGen` → `Analysis`
- Core has no ASP.NET dependency. Depends only on DI abstractions, Logging abstractions, and Options.

**Transport packages contain both sides:**
- Each transport package has an **outbound** client (`HttpTransport`, `SignalRTransport`) and an **inbound** endpoint (`MapConduitEndpoint()`, `ConduitHub`). There is no separate "bridge" concept — browser clients and C# clients use the same transport infrastructure.

**Namespace layout** (within `src/Conduit/`):
- `Conduit.Configuration` — builder, options, endpoints, routes, `TypeFilterBuilder` (`src/Conduit/Configuration/`)
- `Conduit.Dispatch` — pipeline + wire types: `ConduitRuntime`, `DispatchEnvelope`, `DispatchResult`, `IDispatchInterceptor`, `TypeFilterInterceptor`, `TypeFilterOptions` (`src/Conduit/Dispatch/`)
- `Conduit.Routing` — transport: `IConduitTransport` (`src/Conduit/Routing/`)
- `Conduit.Messages`, `Conduit.Handlers`, `Conduit.Pipelines`, `Conduit.Attributes`, `Conduit.Exceptions` — core contracts

**Key design decisions:**
- **Auto-discovery**: The source generator automatically discovers ALL types implementing `ICommand`, `IQuery`, or `IEvent`. No opt-in attributes needed. Use `[NotDispatchable]` to exclude a type.
- **Attribute-based routing**: `[Route("endpointName")]` on a type or `[RouteNamespace("ns", "endpointName")]` on assembly declares compile-time routes. Runtime routes via `conduit.Route("...").To("...")` still work as overrides.
- **Endpoint-based routing**: Messages route to named endpoints via `ConduitBuilder.AddEndpoint()`. Unrouted messages dispatch locally (default). Endpoints define HOW to reach a destination (transport + URI). Routes (attributes or builder) define WHERE types go.
- **DispatchPipeline**: Centralized interceptor chaining + error handling in `Conduit.Dispatch.DispatchPipeline`. Transport packages call `ExecuteRequestAsync` / `ExecuteEventAsync` instead of duplicating pipeline logic.
- **TypeFilterInterceptor**: Access control interceptor in core that rejects dispatch requests for types not in the allowed set. Configured via `AddConduitTypeFilter(filter => filter.AllowType(...))`. Transport-agnostic — works with any inbound path.
- `IWireDispatcher` interface (in `Conduit.Dispatch`) — generated mediator implements this. Methods: `DispatchRequestAsync` (commands/queries) and `DispatchEventAsync` (events). Deserializes wire payloads and calls local handlers.
- `IDispatchInterceptor` interface (in `Conduit.Dispatch`) wraps dispatch calls with a middleware pipeline. Chained by `DispatchPipeline` in reverse registration order.
- `IConduitTransport` is a bidirectional transport with `SendRequestAsync` (request/response) and `SendEventAsync` (fire-and-forget events), both envelope-based.
- Generated `ConduitMediator` implements `IMediator` + `IWireDispatcher`, checks route table before local dispatch.
- **Pipeline behaviors from DI**: `IPipelineBehavior<T,R>` and `IEventPipelineBehavior<T>` are resolved from DI at runtime and chained around handlers/listeners. Register via `services.AddSingleton<IPipelineBehavior<TCommand, TResult>>(...)`.
- **Event echo prevention**: `PublishAsync` dispatches to local listeners AND broadcasts to transports. `PublishLocalAsync` (private) dispatches only to local listeners. Wire-inbound events use `PublishLocalAsync` to prevent re-broadcast loops.
- **Wire discriminator**: `FullTypeName` (e.g. `"Orders.Commands.CreateOrder"`) is used as the type discriminator in `DispatchEnvelope` on the wire.
- **Dual-mode SignalRTransport**: Server mode (IHubContext, pushes events) and client mode (HubConnection, sends commands + receives events). Client mode uses lazy connect (auto-starts on first use via `EnsureConnectedAsync`).
- **ConduitException is transport-agnostic**: Carries a machine-readable `Code` string but no HTTP status code. HTTP status mapping lives in `Transport.Http/EndpointRouteBuilderExtensions.MapStatusCode()`.
- **DispatchResult invariants**: `DispatchResult<T>` uses factory methods `Ok(data)` and `Fail(code, message, details?)` with a `[JsonConstructor]` private constructor for deserialization.
- **TypeScript generation**: Only types marked `[ClientVisible]` get TypeScript interfaces emitted by `Conduit.TypeGen`. This is separate from C# source gen (which includes all types).

## Attributes

| Attribute | Scope | Purpose |
|---|---|---|
| `[NotDispatchable]` | Type | Opt-out from source gen |
| `[Route("endpoint")]` | Type | Route to named endpoint |
| `[RouteNamespace("ns", "endpoint")]` | Assembly | Route namespace to endpoint |
| `[ClientVisible]` | Type | Generate TypeScript for this type |

## Build & Test

```bash
dotnet build          # builds everything including sample apps
dotnet test           # runs all tests across 6 test projects
```

Solution file is `Conduit.slnx` (.NET 10 format).

## Code Style

- Explicit variable names: `serviceProvider` not `sp`, `cancellationToken` not `ct`
- Line breaks for control flow — don't put if-condition and body on the same line
- MSTest v4 + AwesomeAssertions (FluentAssertions fork) for tests
- No mocking libraries — tests use real types, stubs, or in-memory state
- Test project SDK: use `Microsoft.NET.Sdk` (not `MSTest.Sdk`) with `<PackageReference Include="MSTest" />`
- Test directories: `Unit/` for single-class tests, `Integration/` for multi-component tests, `Support/` for test types (one class per file)
- Test class naming: `[Subject]Should` (e.g. `MediatorShould`, `HttpBridgeShould`)
- AAA pattern with `// Arrange`, `// Act`, `// Assert` comments
- Shared test infrastructure in `tests/Conduit.TestUtils/` (`TestHost`, `EventSpy<T>`)

## Project-Specific Notes

### Conduit (core)
- No ASP.NET dependency — only DI abstractions, Logging abstractions, Options
- `DispatchPipeline` — static class centralizing interceptor chaining + error handling
- `TypeFilterInterceptor` — access control interceptor, rejects types not in `TypeFilterOptions.AllowedTypes`
- `AddConduit()` registers `ConduitOptions`. `AddConduitTypeFilter()` registers the interceptor.

### Conduit.Analysis (netstandard2.0)
- Target netstandard2.0 for Roslyn analyzer compatibility
- Use PolySharp NuGet for modern C# features (`required`, file-scoped namespaces, etc.)
- Contains shared type model only (`DispatchableTypeInfo`, `DispatchableKind`, `NamespaceGrouper`)

### Conduit.SourceGen (netstandard2.0)
- `EnforceExtendedAnalyzerRules=true`, `IsRoslynComponent=true`, `NoWarn=RS2008`
- Auto-discovers ALL types implementing `ICommand`, `IQuery`, `IEvent` (no attributes needed)
- Reads `[Route]` and `[RouteNamespace]` attributes for compile-time route registration
- Emits: `ConduitDispatch.g.cs`, `ConduitRegistration.g.cs`, `ConduitMediator.g.cs`
- Generated `AddConduitHandlers()` also registers attribute-declared routes into `ConduitOptions`
- Pipeline behaviors resolved from DI at runtime in generated dispatch code
- Microsoft.CodeAnalysis.CSharp version must be ≤ the SDK's Roslyn version (currently 4.12.0 for .NET 10 SDK)

### Conduit.Transport.Http
- `HttpTransport` — outbound C#→C# request/response via HTTP POST
- `MapConduitEndpoint()` — inbound HTTP dispatch endpoint, maps POST `/api/dispatch` and `/api/dispatch/publish`
- Uses `DispatchPipeline` for interceptor chaining. Maps `DispatchResult` error codes to HTTP status codes.

### Conduit.Transport.SignalR
- `ConduitHub` — inbound SignalR hub, delegates to `DispatchPipeline` for dispatch
- `SignalRTransport` — outbound dual-mode transport with lazy connect (`EnsureConnectedAsync`)
- `AddConduitSignalR()` registers SignalR services and server-mode transport

### Conduit.TypeGen
- MSBuild task using `Microsoft.Build.Utilities.Task`
- `ReflectionScanner.ScanClientVisible()` filters to `[ClientVisible]` types only
- Contains `ReflectionScanner` for post-compilation assembly scanning

### Conduit.Exceptions
- `ConduitException` is the abstract base with a `Code` string
- Concrete: `NotFoundException`, `UnauthorizedException`, `ConflictException` — all sealed

### Sample Apps
- `samples/Conduit.Sample.OrderService/` — browser-facing service with HTTP + SignalR endpoints, routes to InventoryService
- `samples/Conduit.Sample.InventoryService/` — internal service handling inventory commands via SignalR hub
- OrderService has `wwwroot/index.html` with vanilla JS SignalR client

## Design Spec

Full design specification at `docs/design.md` — covers interfaces, generated code patterns, TypeScript output structure, validation strategy, auth model, and future Rebus integration plans.
