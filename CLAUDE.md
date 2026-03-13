# Conduit

Source-generated mediator library for .NET 10 with HTTP/SignalR dispatch, TypeScript codegen, and unified event transport. Replaces MediatR with a reflection-free, compile-time approach.

## Architecture

Eight source projects, six test projects, one sample app:

```
src/Conduit/                        Core interfaces, routing, dispatch, configuration — net10.0
src/Conduit.Analysis/               Shared type model (DispatchableTypeInfo, etc.) — netstandard2.0 (PolySharp)
src/Conduit.SourceGen/              Roslyn incremental source generator + RoslynScanner — netstandard2.0 (PolySharp)
src/Conduit.TypeGen/                MSBuild task for TypeScript emission + ReflectionScanner — net10.0

src/Conduit.Transport.Http/         C#→C# outbound HTTP transport — net10.0
src/Conduit.Transport.SignalR/      C#→C# SignalR (ConduitHub + dual-mode SignalRTransport) — net10.0

src/Conduit.Bridge.Http/            Browser→Server HTTP dispatch endpoint — net10.0
src/Conduit.Bridge.SignalR/         Browser→Server SignalR bridge (interceptors + config) — net10.0
```

**Dependency graph:**
- `Transport.Http` → `Conduit` (core)
- `Transport.SignalR` → `Conduit` (core) + `Microsoft.AspNetCore.SignalR` + `Microsoft.AspNetCore.SignalR.Client`
- `Bridge.Http` → `Conduit` (core) + ASP.NET Core
- `Bridge.SignalR` → `Conduit` (core) + `Microsoft.Extensions.Options` (does NOT reference Transport.SignalR)
- `SourceGen` → `Analysis` → Roslyn APIs
- `TypeGen` → `Analysis`
- Core has no dependency on the other seven projects.

**Transport vs Bridge distinction:**
- **Transports** = C#-to-C# inter-service communication (outbound HTTP client, SignalR hub connections)
- **Bridges** = Browser/TypeScript client communication with the C# backend (inbound endpoints + interceptors)

**Namespace layout** (within `src/Conduit/`):
- `Conduit.Configuration` — builder, options, endpoints, routes (`src/Conduit/Configuration/`)
- `Conduit.Dispatch` — wire-format types: `DispatchEnvelope`, `DispatchResult`, `IDispatchRouter`, `IDispatchInterceptor` (`src/Conduit/Dispatch/`)
- `Conduit.Routing` — transport + resolution: `IConduitTransport`, `IRouteResolver`, `DefaultRouteResolver` (`src/Conduit/Routing/`)
- `Conduit.Messages`, `Conduit.Handlers`, `Conduit.Pipelines`, `Conduit.Attributes`, `Conduit.Exceptions` — core contracts

**Key design decisions:**
- **Endpoint-based routing**: Messages route to named endpoints via `ConduitBuilder.AddEndpoint()` and `Route<T>().To()`. Unrouted messages dispatch locally (default).
- `IDispatchRouter` interface (in `Conduit.Dispatch`) bridges generated code with transport layer via DI. Methods: `DispatchRequestAsync` (commands/queries) and `DispatchEventAsync` (events).
- `IDispatchInterceptor` interface (in `Conduit.Dispatch`) wraps dispatch calls with a middleware pipeline. Used by Bridge packages to add type filtering, auth, etc. without subclassing the hub.
- `IConduitTransport` is a bidirectional transport with `SendRequestAsync` (request/response) and `SendEventAsync` (fire-and-forget events), both envelope-based
- `IRouteResolver` resolves message type → endpoint at runtime; `DefaultRouteResolver` uses namespace prefix matching and throws `InvalidOperationException` if a matched endpoint is missing or has no transport configured
- Generated `ConduitMediator` implements `IMediator` + `IDispatchRouter`, checks route table before local dispatch
- **Event echo prevention**: `PublishAsync` dispatches to local listeners AND broadcasts to transports. `PublishLocalAsync` (private) dispatches only to local listeners. Wire-inbound events use `PublishLocalAsync` to prevent re-broadcast loops.
- **Wire discriminator**: `FullTypeName` (e.g. `"Orders.Commands.CreateOrder"`) is used as the type discriminator in `DispatchEnvelope` on the wire, not the short `TypeName`, to prevent collisions between types with the same name in different namespaces.
- **Single hub with interceptor pipeline**: One `ConduitHub` serves both C# and browser clients. `IDispatchInterceptor` allows Bridge packages to extend hub behavior without subclassing.
- **Dual-mode SignalRTransport**: Server mode (IHubContext, pushes events) and client mode (HubConnection, sends commands + receives events) selected by constructor.
- **Authentication is a transport concern**: Connection-level auth (`RequireAuthorization()` on `MapHub`) applies to all clients. Per-command authorization runs in the dispatch pipeline.
- **ConduitException is transport-agnostic**: Carries a machine-readable `Code` string but no HTTP status code. HTTP status mapping lives in `Bridge.Http/EndpointRouteBuilderExtensions.MapStatusCode()`.
- **DispatchResult invariants**: `DispatchResult<T>` uses factory methods `Ok(data)` and `Fail(code, message, details?)` with a private constructor to ensure Data and Error are never both set.
- `Validate()` is a default interface method — generated code must cast to the interface to call it (e.g., `((ICommand)command).Validate()`)
- `Unsafe.As` used for generic task casting in mediator fallback methods — wrapped in `CastTask<TSource, TTarget>` helper to avoid `ref readonly` warnings
- `ServiceCollectionExtensions.AddConduit()` lives in core `Conduit` namespace (not in a transport package)
- **Void command behavior pipeline**: Uses `VoidToObject` helper to wrap `Task` as `Task<object>` so void commands work correctly with `IPipelineBehavior<T, object>` delegate shape.

## Build & Test

```bash
dotnet build          # builds everything including sample app
dotnet test           # runs all tests across 6 test projects
```

Solution file is `Conduit.slnx` (.NET 10 format).

## Code Style

- Explicit variable names: `serviceProvider` not `sp`, `cancellationToken` not `ct`
- Line breaks for control flow — don't put if-condition and body on the same line
- MSTest v4 + AwesomeAssertions (FluentAssertions fork) for tests
- No NSubstitute currently — tests use real types or in-memory state
- Test project SDK: use `Microsoft.NET.Sdk` (not `MSTest.Sdk`) with `<PackageReference Include="MSTest" />`

## Project-Specific Notes

### Conduit.Analysis (netstandard2.0)
- Target netstandard2.0 for Roslyn analyzer compatibility
- Use PolySharp NuGet for modern C# features (`required`, file-scoped namespaces, etc.)
- Contains shared type model only (`DispatchableTypeInfo`, `DispatchableKind`, `NamespaceGrouper`)
- Scanners have been moved out: `RoslynScanner` → SourceGen, `ReflectionScanner` → TypeGen

### Conduit.SourceGen (netstandard2.0)
- `EnforceExtendedAnalyzerRules=true`, `IsRoslynComponent=true`, `NoWarn=RS2008`
- Contains `RoslynScanner` (moved from Analysis) for compile-time type scanning
- Emits four generated files: `ConduitDispatch.g.cs`, `ConduitRegistration.g.cs`, `ConduitMediator.g.cs`, diagnostics
- When emitting handler calls for queries, parameter name must be `query` (not `command`)
- Roslyn `OriginalDefinition.ToDisplayString()` returns `"Conduit.Messages.ICommand<TResult>"` format (NOT backtick format like `ICommand\`1`)
- Helper classes (`HandlerInfo`, `BehaviorInfo`, `ListenerGroup`) each in their own file
- `HandlerInfo.MessageFullTypeName` is the message type the handler handles (commands and queries alike — not just commands despite the handler interface names)

### Conduit.Transport.Http
- `HttpTransport` implements `IConduitTransport` for outbound C#→C# request/response via HTTP POST
- `EndpointBuilderExtensions` provides `UseHttp()` on `EndpointBuilder`
- Does not support fire-and-forget events (throws `NotSupportedException`)

### Conduit.Transport.SignalR
- `ConduitHub` resolves `IDispatchRouter` and `IDispatchInterceptor` instances from `Context.GetHttpContext()!.RequestServices`, chains interceptors before the router
- `ConduitHub` uses `LoggerMessage` source-generated delegates for error logging in catch-all blocks
- `SignalRTransport` implements `IConduitTransport` with dual-mode: server mode (IHubContext, pushes events) and client mode (HubConnection, outbound requests)
- `EndpointBuilderExtensions` provides `UseSignalR(Uri)` which creates a client-mode HubConnection
- `ServiceCollectionExtensions.AddConduitSignalR()` registers SignalR services and server-mode `SignalRTransport` (registered as concrete type + interface forwarding to support `IAsyncDisposable` disposal)

### Conduit.Bridge.Http
- `EndpointRouteBuilderExtensions.MapConduitBridge()` maps POST `/api/dispatch` and `/api/dispatch/publish` with interceptor pipeline
- Both endpoints handle `ConduitException` with mapped HTTP status codes via `MapStatusCode()`
- Catch-all error handlers log exceptions before returning generic error responses
- Replaces the old `MapConduit()` from the removed `Conduit.Transport.AspNetCore` project

### Conduit.Bridge.SignalR
- `BridgeTypeFilterInterceptor` implements `IDispatchInterceptor` to reject dispatch requests for types not in the allowed set, throws `Conduit.Exceptions.UnauthorizedException`
- `BridgeOptions` holds the `AllowedTypes` HashSet
- `BridgeBuilder` provides `AllowType()` fluent API
- `ServiceCollectionExtensions.AddConduitBridge()` registers interceptors and options
- Does NOT reference `Transport.SignalR` — both reference core Conduit for `IDispatchInterceptor`

### Conduit.TypeGen
- MSBuild task using `Microsoft.Build.Utilities.Task` (fully qualified to avoid ambiguity with `System.Threading.Tasks.Task`)
- Contains `ReflectionScanner` (moved from Analysis) since it runs post-compilation on the assembly DLL
- `ReflectionScanner.FormatFullTypeName()` handles generic result types (e.g. `List<string>`) by building C#-style names instead of using CLR backtick format

### Conduit.Exceptions
- `ConduitException` is the abstract base with a `Code` string (no HTTP status code — that's a transport concern)
- Concrete exceptions: `NotFoundException`, `UnauthorizedException`, `ConflictException` — all sealed
- No `ValidationException` — validation is planned as a separate extension library

### Sample App
- `public partial class Program;` at end of Program.cs enables `WebApplicationFactory<Program>` in integration tests
- Uses static `Dictionary<Guid, string>` for in-memory storage (not thread-safe, fine for demo)
- References: `Conduit`, `Bridge.Http`, `Bridge.SignalR`, `Transport.SignalR`, `SourceGen`, `Analysis`

## Design Spec

Full design specification at `docs/design.md` — covers interfaces, generated code patterns, TypeScript output structure, validation strategy, auth model, and future Rebus integration plans.
