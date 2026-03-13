# Conduit

Source-generated mediator library for .NET 10 with HTTP/SignalR dispatch, TypeScript codegen, and unified event transport. Replaces MediatR with a reflection-free, compile-time approach.

## Architecture

Six source projects, five test projects, one sample app:

```
src/Conduit/                        Core interfaces + routing infrastructure — net10.0
src/Conduit.Analysis/               Shared type model (DispatchableTypeInfo, etc.) — netstandard2.0 (PolySharp)
src/Conduit.SourceGen/              Roslyn incremental source generator + RoslynScanner — netstandard2.0 (PolySharp)
src/Conduit.Transport.AspNetCore/   HTTP dispatch endpoint + HTTP transport — net10.0
src/Conduit.Transport.SignalR/      SignalR hub + SignalR transport — net10.0
src/Conduit.TypeGen/                MSBuild task for TypeScript emission + ReflectionScanner — net10.0
```

**Dependency graph:** SourceGen → Analysis → Roslyn APIs; TypeGen → Analysis; Transport.AspNetCore → Conduit; Transport.SignalR → Conduit. Core has no dependency on the other five projects.

**Namespace layout** (within `src/Conduit/`):
- `Conduit.Configuration` — builder, options, endpoints, routes (`src/Conduit/Configuration/`)
- `Conduit.Dispatch` — wire-format types: `DispatchEnvelope`, `DispatchResult`, `IDispatchRouter` (`src/Conduit/Dispatch/`)
- `Conduit.Routing` — transport + resolution: `IConduitTransport`, `IRouteResolver`, `DefaultRouteResolver` (`src/Conduit/Routing/`)
- `Conduit.Messages`, `Conduit.Handlers`, `Conduit.Pipelines`, `Conduit.Attributes`, `Conduit.Exceptions` — core contracts

**Key design decisions:**
- **Endpoint-based routing**: Messages route to named endpoints via `ConduitBuilder.AddEndpoint()` and `Route<T>().To()`. Unrouted messages dispatch locally (default).
- `IDispatchRouter` interface (in `Conduit.Dispatch`) bridges generated code with transport layer via DI. Methods: `DispatchRequestAsync` (commands/queries) and `DispatchEventAsync` (events).
- `IConduitTransport` is a bidirectional transport with `SendRequestAsync` (request/response) and `SendMessageAsync` (fire-and-forget), both envelope-based
- `IRouteResolver` resolves message type → endpoint at runtime; `DefaultRouteResolver` uses namespace prefix matching
- Generated `ConduitMediator` implements `IMediator` + `IDispatchRouter`, checks route table before local dispatch
- `Validate()` is a default interface method — generated code must cast to the interface to call it (e.g., `((ICommand)command).Validate()`)
- `Unsafe.As` used for generic task casting in mediator fallback methods — wrapped in `CastTask<TSource, TTarget>` helper to avoid `ref readonly` warnings

## Build & Test

```bash
dotnet build          # builds everything including sample app
dotnet test           # runs all 69 tests across 5 test projects
```

Solution file is `Conduit.slnx` (.NET 10 format).

## Code Style

- Explicit variable names: `serviceProvider` not `sp`, `cancellationToken` not `ct`
- Line breaks for control flow — don't put if-condition and body on the same line
- MSTest v4 + AwesomeAssertions (FluentAssertions fork) for tests
- No NSubstitute currently — tests use real types or in-memory state

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

### Conduit.Transport.AspNetCore
- `EndpointRouteBuilderExtensions.MapConduit()` maps POST `/api/dispatch` using `IDispatchRouter`
- `HttpTransport` implements `IConduitTransport` for request/response via HTTP POST
- `EndpointBuilderExtensions` provides `UseHttp()` on `EndpointBuilder`
- `ServiceCollectionExtensions.AddConduit()` registers `ConduitOptions` and `IRouteResolver`

### Conduit.Transport.SignalR
- `ConduitHub` resolves `IDispatchRouter` from `Context.GetHttpContext()!.RequestServices`
- `SignalRTransport` implements `IConduitTransport`, delivers events via `IHubContext<ConduitHub>`
- `EndpointBuilderExtensions` provides `UseSignalR()` on `EndpointBuilder`
- `ServiceCollectionExtensions.AddConduitSignalR()` registers SignalR services and `SignalRTransport`

### Conduit.TypeGen
- MSBuild task using `Microsoft.Build.Utilities.Task` (fully qualified to avoid ambiguity with `System.Threading.Tasks.Task`)
- Contains `ReflectionScanner` (moved from Analysis) since it runs post-compilation on the assembly DLL

### Sample App
- `public partial class Program;` at end of Program.cs enables `WebApplicationFactory<Program>` in integration tests
- Uses static `Dictionary<Guid, string>` for in-memory storage (not thread-safe, fine for demo)

## Design Spec

Full design specification at `docs/design.md` — covers interfaces, generated code patterns, TypeScript output structure, validation strategy, auth model, and future Rebus integration plans.
