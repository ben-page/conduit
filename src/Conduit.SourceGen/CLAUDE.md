# Conduit.SourceGen (netstandard2.0)

- `EnforceExtendedAnalyzerRules=true`, `IsRoslynComponent=true`, `NoWarn=RS2008`
- Auto-discovers ALL types implementing `ICommand`, `IQuery`, `IEvent` across the full compilation graph (no attributes needed). Cross-assembly discovery is intentional for shared contract libraries. Uses `compilation.GlobalNamespace` walk — not incremental, but necessary for cross-assembly support.
- Reads `[LocalOnly]` attribute to suppress event broadcast in generated code
- Reads `[FireAndForget]` attribute: F&F void commands emit `Execute` (background dispatch) + `ExecuteVoidWire` (inline for wire-inbound). F&F events enqueue local listeners to background dispatcher, broadcast to transports inline. CONDUIT003 diagnostic for F&F on result types/queries.
- Reads `[assembly: RemoteContracts("Ns")]` to suppress CONDUIT001 for types in remote contract namespaces
- Emits: `ConduitDispatch.g.cs`, `ConduitRegistration.g.cs`, `ConduitMediator.g.cs`, `ConduitRestEndpoints.g.cs` (conditional)
- Generated `AddConduit()` calls `ConduitRuntime.AddConduitCore()` and registers handlers/listeners by interface type (e.g. `ICommandHandler<T,R>`, `IEventListener<T>`) — enables test substitution
- Generated `AddConduit()` also registers `ConduitTypeRegistry` with `typeof()` expressions for all discovered message types (`eventTypes:`, `commandTypes:`, `queryTypes:`) — compile-time verified, no runtime DI scanning needed by transports
- Pipeline behaviors resolved via `ConduitRuntime.ResolvePipelines` (explicit ordering, not DI enumeration)
- Types without handlers get route-only overloads (throw if no route matches)
- Microsoft.CodeAnalysis.CSharp version must be ≤ the SDK's Roslyn version (currently 4.12.0 for .NET 10 SDK)
