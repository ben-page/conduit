# Conduit

Source-generated mediator library for .NET 10 with HTTP/SignalR dispatch, TypeScript codegen, and unified event transport. Replaces MediatR with a reflection-free, compile-time approach.

## Architecture

Five source projects, five test projects, one sample app:

```
src/Conduit/               Core interfaces (ICommand, IQuery, IEvent, IMediator, etc.) — net10.0
src/Conduit.Analysis/      Shared Roslyn + Reflection scanning — netstandard2.0 (PolySharp)
src/Conduit.SourceGen/     Roslyn incremental source generator — netstandard2.0 (PolySharp)
src/Conduit.AspNetCore/    HTTP endpoint + SignalR hub + event transport — net10.0
src/Conduit.TypeGen/       MSBuild task for TypeScript emission — net10.0
```

**Dependency graph:** SourceGen → Analysis → Roslyn APIs; TypeGen → Analysis; AspNetCore → Conduit → DI abstractions. Core has no dependency on the other four projects.

**Key design decisions:**
- `IMessageRouter` interface (in `Conduit.Routing`) bridges generated code (in consumer assembly) with transport layer (in library assembly) via DI
- `IConduitTransport` interface (in `Conduit.Routing`) is a bidirectional transport with `DeliverAsync` (events) and `SendAsync` (request/response)
- Generated `ConduitMediator` implements both `IMediator` and `IMessageRouter`
- `Validate()` is a default interface method — generated code must cast to the interface to call it (e.g., `((ICommand)command).Validate()`)
- `Unsafe.As` used for generic task casting in mediator fallback methods — wrapped in `CastTask<TSource, TTarget>` helper to avoid `ref readonly` warnings

## Build & Test

```bash
dotnet build          # builds everything including sample app
dotnet test           # runs all 70 tests across 5 test projects
```

Solution file is `Conduit.slnx` (.NET 10 format).

## Code Style

- Explicit variable names: `serviceProvider` not `sp`, `cancellationToken` not `ct`
- Line breaks for control flow — don't put if-condition and body on the same line
- MSTest v4 + AwesomeAssertions (FluentAssertions fork) for tests
- No NSubstitute currently — tests use real types or in-memory state

## Project-Specific Notes

### Conduit.Analysis & Conduit.SourceGen (netstandard2.0)
- Target netstandard2.0 for Roslyn analyzer compatibility
- Use PolySharp NuGet for modern C# features (`required`, file-scoped namespaces, etc.)
- Roslyn `OriginalDefinition.ToDisplayString()` returns `"Conduit.ICommand<TResult>"` format (NOT backtick format like `ICommand\`1`)

### Conduit.SourceGen
- `EnforceExtendedAnalyzerRules=true`, `IsRoslynComponent=true`, `NoWarn=RS2008`
- Emits four generated files: `ConduitDispatch.g.cs`, `ConduitRegistration.g.cs`, `ConduitMediator.g.cs`, diagnostics
- When emitting handler calls for queries, parameter name must be `query` (not `command`)

### Conduit.AspNetCore
- `ConduitEndpoints.MapConduit()` maps POST `/api/dispatch` using `IMessageRouter`
- `ConduitHub` resolves `IMessageRouter` from `Context.GetHttpContext()!.RequestServices`
- `SignalRTransport` implements `IConduitTransport`, delivers events via `IHubContext<ConduitHub>`

### Conduit.TypeGen
- MSBuild task using `Microsoft.Build.Utilities.Task` (fully qualified to avoid ambiguity with `System.Threading.Tasks.Task`)
- Uses `ReflectionScanner` (not `RoslynScanner`) since it runs post-compilation on the assembly DLL

### Sample App
- `public partial class Program;` at end of Program.cs enables `WebApplicationFactory<Program>` in integration tests
- Uses static `Dictionary<Guid, string>` for in-memory storage (not thread-safe, fine for demo)

## Design Spec

Full design specification at `docs/design.md` — covers interfaces, generated code patterns, TypeScript output structure, validation strategy, auth model, and future Rebus integration plans.
