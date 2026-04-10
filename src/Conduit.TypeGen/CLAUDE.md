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
