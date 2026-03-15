# Conduit

Source-generated mediator library for .NET 10 with HTTP/SignalR transport, TypeScript codegen, and unified event transport. Replaces MediatR with a reflection-free, compile-time approach.

## Architecture

Six source projects, six test projects, one shared test utility, four sample apps:

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
- `Conduit.Configuration` — builder, options, endpoints, routes (`src/Conduit/Configuration/`)
- `Conduit.Dispatch` — wire types: `ConduitRuntime`, `DispatchEnvelope`, `DispatchResult`, `IConduitTransport` (`src/Conduit/Dispatch/`)
- `Conduit.Middleware` — pipeline + interceptor types: `IPipelineBehavior`, `IWireInterceptor`, `TypeFilterInterceptor` (`src/Conduit/Middleware/`)
- `Conduit.Messages` — `ICommand`, `IQuery`, `IEvent`, `Unit` (`src/Conduit/Messages/`)
- `Conduit.Handlers` — `ICommandHandler`, `IQueryHandler`, `IEventListener` (`src/Conduit/Handlers/`)
- `Conduit.Attributes` — `LocalOnlyAttribute`, `ClientVisibleAttribute` (`src/Conduit/Attributes/`)
- `Conduit.Exceptions` — `ConduitException`, `NotFoundException`, `UnauthorizedException`, `ConflictException` (`src/Conduit/Exceptions/`)
- `Conduit` (root) — `IMediator`, `IWireMediator` (`src/Conduit/`)

**Key design decisions:**
- **Auto-discovery**: The source generator automatically discovers ALL types implementing `ICommand`, `IQuery`, or `IEvent`. No opt-in attributes needed. Abstract and inaccessible types are excluded automatically.
- **Runtime routing (commands/queries only)**: `conduit.Route("namespace").To("endpoint")` assigns command/query types to named endpoints. Events don't route — they broadcast. Unrouted messages dispatch locally.
- **Endpoint-based routing**: Messages route to named endpoints via `ConduitBuilder.AddEndpoint()`. Endpoints define HOW to reach a destination (transport + URI). Routes define WHERE command/query types go.
- **TypeFilterInterceptor**: Access control interceptor in core that rejects dispatch requests for types not in the allowed set. Configured via `conduit.AddTypeFilter(filter => filter.AllowType(...))`. Transport-agnostic — works with any inbound path.
- `IWireMediator : IMediator` — extended interface for inbound wire dispatch. Adds `HandleWireRequestAsync` and `HandleWireEventAsync`. Transport packages inject this; application code uses `IMediator`.
- `IWireInterceptor` interface (in `Conduit.Middleware`) wraps wire dispatch calls with a middleware pipeline. Chained by `ConduitRuntime` in reverse registration order.
- `IConduitTransport` is a bidirectional transport with `SendRequestAsync` (request/response) and `SendEventAsync` (fire-and-forget events), both envelope-based.
- Generated `ConduitMediator` implements `IWireMediator`, checks route table for commands/queries before local dispatch.
- **Unified pipeline behaviors**: `IPipelineBehavior<TRequest, TResult>` wraps commands, queries, and events uniformly. Register open generics via `conduit.AddPipeline(typeof(MyPipeline<,>))`. Order is explicit (first registered = outermost). Pipeline types are stored in `ConduitOptions.PipelineTypes` and resolved via `ConduitRuntime.ResolvePipelines` — ordering is owned by Conduit, not DI.
- **Event broadcast model**: `PublishAsync` dispatches to local listeners AND broadcasts to all DI-registered `IConduitTransport` instances (server-mode transports). Events marked `[LocalOnly]` skip transport broadcast at compile time. Wire-inbound events use `PublishLocalAsync` (private) to prevent re-broadcast loops.
- **Transport limitations**: HTTP has no server→client push — events only flow client→server over HTTP. SignalR supports bidirectional event flow. Cross-pod unsolicited events require a SignalR backplane.
- **Wire discriminator**: `FullTypeName` (e.g. `"Orders.Commands.CreateOrder"`) is used as the type discriminator in `DispatchEnvelope` on the wire.
- **Dual-mode SignalRTransport**: Server mode (IHubContext, pushes events) and client mode (HubConnection, sends commands + receives events). Client mode uses lazy connect (auto-starts on first use via `EnsureConnectedAsync`).
- **ErrorCode enumeration class**: `ConduitException.Code` is an `ErrorCode` record that co-locates the machine-readable code string with its HTTP status code. Equality is based on `Value` only. RESTful endpoints use `Code.HttpStatusCode` directly for Problem Details responses. The envelope endpoint uses `MapStatusCode()` to map the string-based `DispatchError.Code` from the wire format.
- **DispatchResult invariants**: `DispatchResult<T>` uses factory methods `Ok(data)` and `Fail(code, message, details?)` with a `[JsonConstructor]` private constructor for deserialization.
- **TypeScript generation**: Types marked `[ClientVisible]` get TypeScript interfaces emitted by `Conduit.TypeGen`. This includes both dispatchable types and non-dispatchable DTOs/models. TypeGen emits a self-contained runtime (`conduit.ts`) with transports and a per-project file (`conduit.generated.ts`) with `ConduitClient` — no npm package needed. `ConduitClient` is a thin dispatch client (not a mediator) with `execute`/`fetch`/`publish`/`on` methods.
- **Wire dispatch route forwarding**: The generated `DispatchRequestAsync` checks routes before the local type switch. Unknown types matching a route are forwarded as raw envelopes to the transport without deserializing locally.

## Attributes

| Attribute | Scope | Purpose |
|---|---|---|
| `[LocalOnly]` | Type | Prevent event from being broadcast to transports |
| `[ClientVisible]` | Type | Generate TypeScript for this type |
| `[RemoteContracts("Ns")]` | Assembly | Suppress CONDUIT001 for types in namespace (remote contract types without local handlers) |

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
- Test class naming: `[Subject]Should` (e.g. `MediatorShould`, `RoutingShould`)
- AAA pattern with `// Arrange`, `// Act`, `// Assert` comments
- Shared test infrastructure in `tests/Conduit.TestUtils/` (`TestHost`, `EventSpy<T>`)

## Project-Specific Notes

### Conduit (core)
- No ASP.NET dependency — only DI abstractions, Logging abstractions, Options
- `ConduitRuntime` — static class centralizing route resolution, pipeline chaining, interceptor chaining, error handling
- `TypeFilterInterceptor` (in `Conduit.Middleware`) — access control interceptor, rejects types not in `TypeFilterOptions.AllowedTypes`
- `AddConduit()` is generated by source gen. All configuration goes through `ConduitBuilder`: `AddEndpoint`, `Route`, `AddPipeline`, `AddTypeFilter`.

### Conduit.Analysis (netstandard2.0)
- Target netstandard2.0 for Roslyn analyzer compatibility
- Use PolySharp NuGet for modern C# features (`required`, file-scoped namespaces, etc.)
- Contains shared type model only (`DispatchableTypeInfo`, `DispatchableKind`, `NamespaceGrouper`)

### Conduit.SourceGen (netstandard2.0)
- `EnforceExtendedAnalyzerRules=true`, `IsRoslynComponent=true`, `NoWarn=RS2008`
- Auto-discovers ALL types implementing `ICommand`, `IQuery`, `IEvent` (no attributes needed)
- Reads `[LocalOnly]` attribute to suppress event broadcast in generated code
- Emits: `ConduitDispatch.g.cs`, `ConduitRegistration.g.cs`, `ConduitMediator.g.cs`, `ConduitRestEndpoints.g.cs` (conditional)
- Generated `AddConduit()` calls `ConduitRuntime.AddConduitCore()` and registers handlers
- Pipeline behaviors resolved via `ConduitRuntime.ResolvePipelines` (explicit ordering, not DI enumeration)
- Types without handlers get route-only overloads (throw if no route matches)
- Microsoft.CodeAnalysis.CSharp version must be ≤ the SDK's Roslyn version (currently 4.12.0 for .NET 10 SDK)

### Conduit.Transport.Http
- `HttpTransport` — outbound C#→C# request/response via HTTP POST
- `MapConduitEndpoint()` — inbound HTTP envelope dispatch endpoint, maps POST `/api/dispatch` and `/api/dispatch/publish`
- `ConduitRestHandlers` — typed handler helpers for RESTful endpoint mapping (query→route binding, command→body binding)
- RESTful extension methods (`MapConduitGet<T>`, `MapConduitPost<T>`, etc.) are source-generated into `ConduitRestEndpoints` — conditionally emitted only when `Conduit.Transport.Http` is referenced
- Maps `DispatchResult` error codes to HTTP status codes (envelope endpoint); `ConduitException` → Problem Details (RESTful endpoints)

### Conduit.Transport.SignalR
- `ConduitHub` — inbound SignalR hub with `Dispatch` and `Publish` methods, delegates to `IWireMediator`
- `SignalRTransport` — outbound dual-mode transport with lazy connect (`EnsureConnectedAsync`)
- `AddConduitSignalR()` registers SignalR services and server-mode transport as `IConduitTransport`

### Conduit.TypeGen
- MSBuild task using `Microsoft.Build.Utilities.Task`
- `ReflectionScanner.ScanClientVisible()` filters to `[ClientVisible]` types — includes both dispatchable types (commands/queries/events) and non-dispatchable types (DTOs/models) with `DispatchableKind.Model`
- Emits two files: `conduit.ts` (stable runtime) and `conduit.generated.ts` (per-project types + client)
- `conduit.ts` contains: `DispatchResult<T>`, `ConduitError`, `ConduitTransport` interface, `HttpTransport`, conditionally `SignalRTransport`
- `conduit.generated.ts` contains: type interfaces, `CommandMap`/`QueryMap`/`EventMap` (keys are `FullTypeName`), `ConduitClient` class, re-exports
- `ConduitClient` — typed browser client with `execute` (commands), `fetch` (queries), `publish` (client→server events), `on` (event subscriptions returning `Subscription`)
- `ConduitClient` returns `DispatchResult<T>` (does not throw) — consumers decide error handling strategy
- `Conduit.TypeGen.targets` — MSBuild targets file with opt-in via `<ConduitTypeGenEnabled>true</ConduitTypeGenEnabled>`, configurable output dir (`ConduitTypeGenOutput`, default `ClientApp/generated`) and transports (`ConduitTransports`, default `Http;SignalR`)
- TypeScript transport interface mirrors C#: `sendRequest` (SendRequestAsync), `sendEvent` (SendEventAsync), `subscribe` (browser-only)
- `HttpTransport` uses native `fetch`, zero npm dependencies. `SignalRTransport` wraps `HubConnection` from `@microsoft/signalr`
- Void commands merged into `CommandMap` with `response: void` (no separate `VoidCommandMap`)

### Conduit.Exceptions
- `ErrorCode` — enumeration class pairing a machine-readable code string with HTTP status code. Value-based equality. Built-in codes: `NotFound`, `Unauthorized`, `Conflict`, `InternalError`, `BadRequest`. Consumers define their own.
- `ConduitException` — abstract base with `ErrorCode Code` property
- Concrete: `NotFoundException`, `UnauthorizedException`, `ConflictException` — all sealed

### Sample Apps
- `samples/browser-client/Conduit.Sample.Notes/` — browser-facing single service with SignalR hub, notes CRUD, events pushed to browser. Vite + TypeScript frontend in `ClientApp/` consuming generated `ConduitClient` + `SignalRTransport`
- `samples/service-to-service/Conduit.Sample.Orders/` — order service that routes commands to InventoryService via SignalR, exposes HTTP dispatch
- `samples/service-to-service/Conduit.Sample.Inventory/` — internal inventory service handling commands via SignalR hub
- `samples/rest-api/Conduit.Sample.Todos/` — REST API demonstrating `MapConduitGet/Post/Put/Delete` with OpenAPI/Swagger UI

## Design Spec

Original design specification at `docs/design.md` — covers interfaces, generated code patterns, TypeScript output structure, validation strategy, auth model, and future Rebus integration plans. Some sections are stale; this file (`CLAUDE.md`) has the current architecture summary.
