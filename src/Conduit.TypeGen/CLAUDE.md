# Conduit.TypeGen

- MSBuild task using `Microsoft.Build.Utilities.Task` + `System.Reflection.MetadataLoadContext`
- Uses `MetadataLoadContext` (not `Assembly.LoadFrom`) to scan assemblies — supports apps referencing ASP.NET shared framework types without needing them in the output directory. Resolves assemblies from: app output dir, .NET runtime dir, ASP.NET Core shared framework dir.
- `ReflectionScanner.ScanClientVisible()` filters to `[ClientVisible]` types — includes both dispatchable types (commands/queries/events) and non-dispatchable types (DTOs/models) with `DispatchableKind.Model`. Uses `GetCustomAttributesData()` (not `GetCustomAttributes()`) for MetadataLoadContext compatibility.
- `TypeCollector` — BFS walker that recursively discovers all complex types referenced by `[ClientVisible]` type properties AND query/command result types. Unwraps collections, nullable, and arrays to find the inner element type. Skips primitives, enums, and framework types. This means Domain-layer DTOs referenced by Application-layer commands/queries are automatically included in the generated TypeScript — no `[ClientVisible]` attribute needed on Domain types.
- `TypeMapper` — Maps .NET types to TypeScript equivalents using string-based name comparisons (not `typeof()`) for MetadataLoadContext compatibility. Handles: primitives, Nullable, arrays, List/IList/IEnumerable/IReadOnlyList, Dictionary/IDictionary/IReadOnlyDictionary, enums (→ string literal unions via `GetFields()`), complex types (→ type name).
- Emits two files: `conduit.ts` (stable runtime) and `conduit.generated.ts` (per-project types + client)
- `conduit.ts` contains: `DispatchResult<T>`, `ConduitError`, `ConduitTransport` interface, `HttpTransport`, conditionally `SignalRTransport`
- `conduit.generated.ts` contains: type interfaces (root `[ClientVisible]` types + transitively-discovered types), `CommandMap`/`QueryMap`/`EventMap` (keys are `FullTypeName`), `ConduitClient` class, re-exports
- `ConduitClient` — typed browser client with `execute` (commands), `fetch` (queries), `publish` (client→server events), `on` (event subscriptions returning `Subscription`)
- `ConduitClient` returns `DispatchResult<T>` (does not throw) — consumers decide error handling strategy
- `Conduit.TypeGen.targets` — MSBuild targets file with opt-in via `<ConduitTypeGenEnabled>true</ConduitTypeGenEnabled>`, configurable output dir (`ConduitTypeGenOutput`, default `ClientApp/generated`) and transports (`ConduitTransports`, default `Http;SignalR`)
- **Multi-project support**: For Clean Architecture projects where `[ClientVisible]` types are in a library assembly (not the Web host), consumers can override `AssemblyPath` in the target to point at the library DLL. Example: `AssemblyPath="$(MSBuildProjectDirectory)\bin\$(Configuration)\net10.0\MyApp.Application.dll"`
- TypeScript transport interface mirrors C#: `sendRequest` (SendRequestAsync), `sendEvent` (SendEventAsync), `subscribe` (browser-only)
- `HttpTransport` uses native `fetch`, zero npm dependencies. `SignalRTransport` wraps `HubConnection` from `@microsoft/signalr`
- Void commands merged into `CommandMap` with `response: void` (no separate `VoidCommandMap`)
- **Inheritance**: abstract `[ClientVisible]` base types emit plain TypeScript `extends` — the derived interface extends the base and declares only its own properties. Concrete derived types are auto-discovered with no extra attribute (including types reached only through abstract-typed properties); multi-level chains work. `[LocalOnly]` on a derived type excludes it. `DispatchableTypeInfo` carries `BaseFullTypeName`/`BaseTypeName`; `ScanClientVisible` lets abstract `[ClientVisible]` bases through; `MergeFileEmitter` emits the `extends` clause and declared-only properties. Intentionally NOT supported: discriminated-union type aliases, literal discriminator fields, runtime type-guard helpers, open-generic inheritance.
- **Declared refusals (`ErrorMap`, `isError`)**: `ErrorTypeScanner.Scan(assemblies)` finds concrete `ConduitException`
  subclasses that declare at least one public instance property on themselves or an ancestor below
  `ConduitException` — the same walk as `Conduit.Exceptions.ConduitException.ClientDataProperties`, but
  re-implemented by full NAME (`type.FullName != "Conduit.Exceptions.ConduitException"`) because TypeGen runs
  under `MetadataLoadContext`, where a `typeof(ConduitException)` comparison against a `MetadataLoadContext`
  type is always false. Like `ReflectionScanner`/`TypeCollector`, it calls `assembly.GetExportedTypes()`
  directly — no `ReflectionTypeLoadException` guard.
  - `MergeFileEmitter.Emit` calls it right after `TypeMapEmitter.EmitMaps`, and emits, into
    `conduit.generated.ts`: one `export interface <ExceptionTypeName> { ... }` per found type (properties
    camelCased and mapped by `TypeMapper`, same as any other emitted interface); `export interface ErrorMap {
    '<Full.Type.Name>': <ExceptionTypeName>; ... }` (`{}` when nothing was found); and the narrowing helper
    `export function isError<K extends keyof ErrorMap>(error: unknown, type: K): error is { type: K; data:
    ErrorMap[K] }`.
  - `conduit.ts`'s `ConduitError` carries `status: number` (the HTTP-status category; 0 from a server that
    predates it), `type?: string` (a declared refusal's full type name), and `data?: unknown`.
  - **camelCase parity with the wire**: the emitted interface keys use `TypeScriptEmitter.ToCamelCase`; the
    wire (`ConduitException.GetClientData`) keys with `JsonNamingPolicy.CamelCase.ConvertName`. These are two
    separate implementations, so they could in principle disagree — checked exhaustively (every combination of
    upper/lower/digit/`_` up to 8 characters, i.e. every possible C# identifier shape) and found to always
    agree. If either ever changes, re-run that comparison before trusting this note.
  - **Complex-typed data properties are not auto-collected.** A data property whose type is not primitive
    (string, number, boolean, or a collection of those) is mapped by `TypeMapper` to a bare type name, but this
    walk does not feed `TypeCollector` — the type only gets an emitted interface if something else makes it
    `[ClientVisible]`. An un-emitted name fails the CONSUMER'S TypeScript compile, which is visible and cheap
    to fix, but keep refusal data to primitives, strings, and collections of them to avoid it.
  - **Interface-name collisions are a hard build-time error, not a silent merge.** `Scan` dedupes only by FULL
    type name, so two `ConduitException` subclasses in different namespaces that share a short name would each
    get their own `ErrorMap` entry but collide on the same `export interface {ShortName}` — TypeScript would
    silently merge the two declarations (masking a real mismatch) or fail the consumer's compile, neither of
    which is a signal at generation time. Symmetrically, an error type's short name can collide with a
    DTO/command/query/event interface this file already emitted. `MergeFileEmitter.Emit` tracks every
    interface identifier it emits (name → full type name) as it goes and hands that to
    `MergeFileEmitter.ThrowIfErrorTypeNameCollides` (called from `EmitErrorTypes`), which throws
    `InvalidOperationException` naming both colliding full type names for either case. TypeGen has no
    dedicated exception type for a hard generation error — every one surfaces this way, since
    `TypeGenTask.Execute` catches any `Exception` and reports it via `Log.LogError`. `ThrowIfErrorTypeNameCollides`
    is `internal` (`InternalsVisibleTo` → `Conduit.TypeGen.Tests`) and unit-tested directly rather than by
    adding a colliding pair of sample types to the shared test project: `Scan` always scans the whole
    assembly, so a permanent collision anywhere in it would trip this guard for every OTHER test that scans
    that assembly too, not just the one that wants to see it fire.
