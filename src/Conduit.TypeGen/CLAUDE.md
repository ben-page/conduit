# Conduit.TypeGen

- MSBuild task using `Microsoft.Build.Utilities.Task` + `System.Reflection.MetadataLoadContext`
- Uses `MetadataLoadContext` (not `Assembly.LoadFrom`) to scan assemblies — supports apps referencing ASP.NET shared framework types without needing them in the output directory. Resolves assemblies from: app output dir, .NET runtime dir, ASP.NET Core shared framework dir.
- `ReflectionScanner.ScanClientVisible()` filters to `[ClientVisible]` types — includes both dispatchable types (commands/queries/events) and non-dispatchable types (DTOs/models) with `DispatchableKind.Model`. Uses `GetCustomAttributesData()` (not `GetCustomAttributes()`) for MetadataLoadContext compatibility.
- `TypeCollector` — BFS walker that recursively discovers all complex types referenced by `[ClientVisible]` type properties AND query/command result types. Unwraps collections, nullable, and arrays to find the inner element type. Skips primitives, enums, closed sets, and framework types. This means Domain-layer DTOs referenced by Application-layer commands/queries are automatically included in the generated TypeScript — no `[ClientVisible]` attribute needed on Domain types.
- **Closed sets** (`ClosedSetReader`): a type carrying `[ClosedSet("a", …)]` (`Conduit.Attributes`) and implementing
  `IClosedSet<itself>` (`Conduit.Messages`) maps to `'a' | …` — its declared wire VALUES, single-quoted and escaped
  (`ToTsStringLiteral`), never its member names — and gets no interface. Both are matched by full NAME
  (MetadataLoadContext). The values come from the attribute because a MetadataLoadContext cannot run the static
  initializers that create the members (`FieldInfo.GetValue` throws there — measured on DMS's `PermissionScope`,
  2026-10-09). The attribute on a type that does not implement the interface, or with no values, is a hard generation
  error (`InvalidOperationException`). The reverse — the interface without the attribute — and an attribute list that
  differs from `All` are caught at RUNTIME, by `ClosedSetJsonConverterFactory` on the type's first serialization:
  TypeGen cannot see `All`. A closed set is reached like any property type and needs no `[ClientVisible]`. Enum output
  is untouched by it.
- `TypeMapper` — Maps .NET types to TypeScript equivalents using string-based name comparisons (not `typeof()`) for MetadataLoadContext compatibility. Handles: primitives, Nullable, arrays, List/IList/IEnumerable/IReadOnlyList, Dictionary/IDictionary/IReadOnlyDictionary, enums (→ string literal unions of member names via `GetFields()`), closed sets (→ string literal unions of their `[ClosedSet]` values, `ClosedSetReader`), complex types (→ type name).
- `ResultNullabilityReader` / `TypeMapEmitter.ResolveResultType` — a query or command result declared `T?` emits `response: T | null`. Read from the dispatchable's `IQuery<>`/`ICommand<>` interface row `NullableAttribute` (reflection cannot see it) by `ReflectionScanner` into `DispatchableTypeInfo.ResultIsNullable`; top level only (element nullability such as `List<T?>` is out of scope), a result declared on a constructed generic base is not read (it emits as before), an oblivious result is not nullable, and a value-type `T?` is mapped by `TypeMapper` through `Nullable<T>` so ` | null` is never doubled.
- Emits two files: `conduit.ts` (stable runtime) and `conduit.generated.ts` (per-project types + client)
- `conduit.ts` contains: `DispatchResult<T>`, `ConduitError`, `ConduitTransport` interface, `HttpTransport`, conditionally `SignalRTransport`
- **The transport is real TypeScript** — `Transport/base.ts`, `http.ts`, `signalr.ts`, embedded in this DLL as
  `Conduit.TypeGen.Transport.<file>` (one `EmbeddedResource` glob). `BaseFileEmitter` assembles `conduit.ts` from
  them through `TransportSource`: the header line; every package import, hoisted, in file order (SignalR's two only
  when `Transports` includes SignalR); then each file's body verbatim plus one blank line — `base.ts`, `http.ts`,
  `signalr.ts`. An import of another transport file (`./base`) is dropped: `conduit.ts` is one module, and those
  imports exist so each file type-checks alone. **The file shape is enforced** (`TransportSource.Parse`, a generation
  error): single-line imports at the top, then the body, no import below it. A header comment would be emitted as
  body, so a transport file has none. **Path anchors:** `http.ts`'s `'/api/conduit'` and `signalr.ts`'s `'/conduit'`
  each appear exactly once; TypeGen swaps in `BasePath`/`HubPath` file by file (raw, unescaped). CRLF and a BOM in a
  resource never reach the output. **Tests:** behavior in `tests/transport-ts` (strict `tsc` + vitest; CI job
  `transport-ts`), assembly in `TransportSourceShould`/`BaseFileEmitterShould`. The assembled output (default and
  HTTP-only) is pinned by the goldens in `tests/transport-ts/emitted/` and type-checked there, so **a transport edit
  fails that test until the goldens are regenerated — the golden diff is what every consumer's `conduit.ts` will
  show.** **An edit to these files changes every consumer's committed `conduit.ts` on its next build** (ManagementSystem
  and ELoc load this DLL by path) — land it as an emitter change. `Transport/tsconfig.json` only points an editor at
  `tests/transport-ts`'s config; never `npm i` under `src/`.
- `conduit.generated.ts` contains: type interfaces (root `[ClientVisible]` types + transitively-discovered types), `CommandMap`/`QueryMap`/`EventMap` (keys are `FullTypeName`), `ConduitClient` class, re-exports
- `ConduitClient` — typed browser client with `execute` (commands), `fetch` (queries), `publish` (client→server events), `on` (event subscriptions returning `Subscription`)
- `ConduitClient` returns `DispatchResult<T>` (does not throw) — consumers decide error handling strategy
- `Conduit.TypeGen.targets` — MSBuild targets file with opt-in via `<ConduitTypeGenEnabled>true</ConduitTypeGenEnabled>`, configurable output dir (`ConduitTypeGenOutput`, default `ClientApp/generated`) and transports (`ConduitTransports`, default `Http;SignalR`)
- **Multi-project support**: For Clean Architecture projects where `[ClientVisible]` types are in a library assembly (not the Web host), consumers can override `AssemblyPath` in the target to point at the library DLL. Example: `AssemblyPath="$(MSBuildProjectDirectory)\bin\$(Configuration)\net10.0\MyApp.Application.dll"`
- TypeScript transport interface mirrors C#: `sendRequest` (SendRequestAsync), `sendEvent` (SendEventAsync), `subscribe` (browser-only)
- `HttpTransport` uses native `fetch`, zero npm dependencies, and drops the caller's `X-Conduit-Message-Id`/`-Conversation-Id`/`-Causation-Id` headers (any case; `callerHeaders`): a browser is a conversation's origin, so those ids are the server's. Other caller headers still pass, and can still override `Content-Type`/`X-Conduit-Type`. `SignalRTransport` wraps `HubConnection` from `@microsoft/signalr` and passes a null hub context
- Void commands merged into `CommandMap` with `response: void` (no separate `VoidCommandMap`)
- **Inheritance**: abstract `[ClientVisible]` base types emit plain TypeScript `extends` — the derived interface extends the base and declares only its own properties. Concrete derived types are auto-discovered with no extra attribute (including types reached only through abstract-typed properties); multi-level chains work. `[LocalOnly]` on a derived type, or on a type it derives from, excludes it (`ReflectionScanner.IsLocalOnly` walks `BaseType`, as the source generator does; `TypeCollector` uses it too). `DispatchableTypeInfo` carries `BaseFullTypeName`/`BaseTypeName`; `ScanClientVisible` lets abstract `[ClientVisible]` bases through; `MergeFileEmitter` emits the `extends` clause and declared-only properties. Intentionally NOT supported: discriminated-union type aliases, literal discriminator fields, runtime type-guard helpers, open-generic inheritance.
- **Declared refusals (`ErrorMap`, `isError`)**: `ErrorTypeScanner.Scan(assemblies)` finds concrete `ConduitException`
  subclasses that are CLIENT-TYPED — `ConduitException.IsClientTyped`'s rule, re-stated by name because TypeGen
  runs under `MetadataLoadContext`: declared in any assembly OTHER than `Conduit.Contracts` (a consumer's own
  refusal — data-less or not; the assembly-name check is `ErrorTypeScanner.ConduitContractsAssemblyName`, `internal`
  so `Conduit.TypeGen.Tests` can pin it to the real assembly name and catch a rename), OR declaring at least one
  public instance property on themselves or an ancestor below `ConduitException` (the same walk as
  `Conduit.Exceptions.ConduitException.ClientDataProperties`, re-implemented by full NAME —
  `type.FullName != "Conduit.Exceptions.ConduitException"` — because a `typeof(ConduitException)` comparison
  against a `MetadataLoadContext` type is always false). A data-less consumer refusal (outside Conduit.Contracts,
  declares nothing) is still found by the first half of the OR and gets an empty `export interface {}` and an
  `ErrorMap` entry — same treatment as one that declares data, just with no properties. Like
  `ReflectionScanner`/`TypeCollector`, it calls `assembly.GetExportedTypes()` directly — no
  `ReflectionTypeLoadException` guard, **and no non-exported types**: a `private` or nested `ConduitException`
  subclass can be `IsClientTyped` (typed on the wire, `Type`/`Data` sent) yet never reach `ErrorMap` or get an
  interface, because `GetExportedTypes()` never surfaces it to the scanner — see `docs/error-handling.md` §
  "Client-typed refusals".
  - `MergeFileEmitter.Emit` calls it right after `TypeMapEmitter.EmitMaps`, and emits, into
    `conduit.generated.ts`: one `export interface <ExceptionTypeName> { ... }` per found type (properties
    camelCased and mapped by `TypeMapper`, same as any other emitted interface; `{}` when the type declares no
    properties); `export interface ErrorMap { '<Full.Type.Name>': <ExceptionTypeName>; ... }` (`{}` when nothing
    was found); and the narrowing helper `export function isError<K extends keyof ErrorMap>(error: unknown, type:
    K): error is { type: K; data: ErrorMap[K] }`.
  - `conduit.ts`'s `ConduitError` carries `status: number` (the HTTP-status category; 0 from a server that
    predates it), `type?: string` (a declared refusal's full type name), `data?: unknown`, and `reference?: string` (an unexpected failure's support reference; optional, absent on refusals
    and from older servers).
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
    which is a signal at generation time. The same collision is possible against: a DTO/command/query/event
    interface this file already emitted; `CommandMap`/`QueryMap`/`EventMap` (present only when that kind has
    at least one type — `TypeMapEmitter.EmitMaps` now RETURNS the names it actually emitted, the source of
    truth for those three, rather than a second guess mirroring its own conditionals); or any other fixed
    name this file always declares, imports, or re-exports regardless of input — the static import line,
    `ConduitClient`, `AllCommands`/`AllQueries`/`AllEvents`, the bottom re-export line, `ErrorMap`, `isError`
    (`MergeFileEmitter.FixedMergeFileNames` — necessarily a hand list, since these are string literals with
    no single emission point to derive from; kept right above `Emit`, next to the literals it mirrors).
    `MergeFileEmitter.EmitInterfacesAndMaps` builds one reserved-name set from all of these (name → full type
    name, or a description for a fixed name) and hands it to `MergeFileEmitter.ThrowIfErrorTypeNameCollides`
    (called from `EmitErrorTypes`), which throws `InvalidOperationException` naming both colliding full type
    names (or descriptions) for any of these cases. TypeGen has no dedicated exception type for a hard
    generation error — every one surfaces this way, since `TypeGenTask.Execute` catches any `Exception` and
    reports it via `Log.LogError`. Both `EmitInterfacesAndMaps` and `ThrowIfErrorTypeNameCollides` are
    `internal` (`InternalsVisibleTo` → `Conduit.TypeGen.Tests`) and unit-tested directly with real production
    inputs rather than by adding a colliding pair of sample types to the shared test project: `Scan` always
    scans the whole assembly, so a permanent collision anywhere in it would trip this guard for every OTHER
    test that scans that assembly too, not just the one that wants to see it fire.
