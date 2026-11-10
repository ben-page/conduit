# Conduit

Source-generated mediator library for .NET 10 with HTTP/SignalR transport, TypeScript codegen, and unified event transport. Replaces MediatR with a reflection-free, compile-time approach.

## Architecture

Twenty source projects, fourteen test projects, one shared test utility, one TypeScript test project (`tests/transport-ts`), four sample apps:

```
src/Conduit/                        Core interfaces, routing, dispatch pipeline, configuration — net10.0
src/Conduit.Contracts/              Dependency-free contract types: Conduit.Exceptions, Conduit.Messages (minus ConduitFile), Conduit.Attributes — net10.0, zero package references, referenced by Conduit
src/Conduit.Analysis/               Shared type model (DispatchableTypeInfo, etc.) — netstandard2.0 (PolySharp)
src/Conduit.SourceGen/              Roslyn incremental source generator + RoslynScanner — netstandard2.0 (PolySharp)
src/Conduit.TypeGen/                MSBuild task for TypeScript emission + ReflectionScanner — net10.0; the TS transport's source in `Transport/*.ts` (embedded)

src/Conduit.Transport.Http/              HTTP transport (outbound HttpTransport + inbound MapConduitEndpoint) — net10.0
src/Conduit.Transport.SignalR/           SignalR transport (outbound SignalRTransport + inbound ConduitHub) — net10.0
src/Conduit.Transport.AzureServiceBus/   Azure Service Bus transport (IConduitTransport + IHostedService) — net10.0
src/Conduit.Transport.RabbitMQ/          RabbitMQ transport (IConduitTransport + IHostedService) — net10.0
src/Conduit.Transport.AmazonSqs/         Amazon SQS (+SNS) transport (IConduitTransport + IHostedService) — net10.0
src/Conduit.Transport.ActiveMQ/          ActiveMQ Artemis transport (IConduitTransport + IHostedService) — net10.0
src/Conduit.Durability/                 Durable messaging — transactional outbox + inbox (idempotent dedup) + dead-letter queue — net10.0
src/Conduit.Audit/                      Audit trail — observation consolidation, batching, local store — net10.0
src/Conduit.Jobs/                       Background jobs — cron/interval schedules + event triggers, cluster-safe via row claiming — net10.0
src/Conduit.Resilience/                 Resilience pipeline behavior — Polly v8 retry/circuit-breaker over dispatch — net10.0

src/Conduit.Persistence/                Engine-agnostic relational persistence abstraction (ISqlDialect, IConduitConnectionFactory, DbStoreBase) — net10.0
src/Conduit.Persistence.Sqlite/         SQLite engine: SqliteConduitConnectionFactory, SqliteDialect, UseSqlite — net10.0
src/Conduit.Persistence.Postgres/       PostgreSQL engine: PostgresConduitConnectionFactory, PostgresDialect, UsePostgres — net10.0
src/Conduit.Persistence.SqlServer/      SQL Server engine: SqlServerConduitConnectionFactory, SqlServerDialect, UseSqlServer — net10.0
src/Conduit.Persistence.MySql/          MySQL engine: MySqlConduitConnectionFactory, MySqlDialect, UseMySql — net10.0
```

**Dependency graph:**
- `Transport.Http` → `Conduit` (core) + ASP.NET Core
- `Transport.SignalR` → `Conduit` (core) + ASP.NET Core + SignalR
- `Transport.AzureServiceBus` → `Conduit` (core) + Azure.Messaging.ServiceBus
- `Transport.RabbitMQ` → `Conduit` (core) + RabbitMQ.Client
- `Transport.AmazonSqs` → `Conduit` (core) + AWSSDK.SimpleNotificationService + AWSSDK.SQS
- `Transport.ActiveMQ` → `Conduit` (core) + AMQPNetLite.Core
- `Durability` → `Conduit` (core) + `Conduit.Persistence` (no direct DB-driver dependency; engine resolved from DI)
- `Audit` → `Conduit` (core) + `Conduit.Persistence` (no direct DB-driver dependency; engine resolved from DI, shared with Durability)
- `Jobs` → `Conduit` (core) + `Conduit.Persistence` + Cronos (no direct DB-driver dependency; engine resolved from DI, shared with Durability and Audit)
- `Resilience` → `Conduit` (core) + Polly (via Microsoft.Extensions.Resilience)
- `Persistence` → `Conduit` (core) + DI/Hosting abstractions (no DB driver)
- `Persistence.Sqlite` → `Persistence` + Microsoft.Data.Sqlite
- `Persistence.Postgres` → `Persistence` + Npgsql
- `Persistence.SqlServer` → `Persistence` + Microsoft.Data.SqlClient
- `Persistence.MySql` → `Persistence` + MySqlConnector
- `SourceGen` → `Analysis` → Roslyn APIs
- `TypeGen` → `Analysis`
- Core has no ASP.NET dependency. Depends only on DI abstractions, Hosting abstractions, Logging abstractions, Options, and `Microsoft.Extensions.Configuration.Binder` (for binding `ConduitOptions` and transport options from `IConfiguration`). `Conduit.Contracts` still carries zero package references.

**Transport packages contain both sides:**
- Each transport package has an **outbound** client (`HttpTransport`, `SignalRTransport`) and an **inbound** endpoint (`MapConduitEndpoint()`, `ConduitHub`). There is no separate "bridge" concept — browser clients and C# clients use the same transport infrastructure.

**Namespace layout** (split across `src/Conduit/` and `src/Conduit.Contracts/` — namespaces do NOT
follow assembly boundaries here; `Conduit.Contracts.csproj` sets `RootNamespace=Conduit` so its types
keep their original `Conduit.*` names):
- `Conduit.Configuration` — builder, options, endpoints, routes, `TopologyMode` (`src/Conduit/Configuration/`)
- `Conduit.Runtime` — wire types + runtime: `ConduitRuntime`, `TransportDispatcher`, `DispatchEnvelope`, `DispatchResult`, `IConduitTransport`, `MessageContext`, `TopologyNaming`, `ConduitTypeRegistry` (`src/Conduit/Runtime/`)
- `Conduit.Middleware` — pipeline, gate, and interceptor types: `IPipelineBehavior`, `IInboundInterceptor` (gate), `IOutboundInterceptor`, `TypeFilterInterceptor` (`src/Conduit/Middleware/`)
- `Conduit.Messages` — spans **two assemblies**. `ICommand`, `IQuery`, `IEvent`, `Unit`, `IClosedSet<TSelf>` live in `src/Conduit.Contracts/Messages/`. `ConduitFile` stays behind in `src/Conduit/Messages/` (core) — it holds a `BinaryData` field, which needs the `System.Memory.Data` package, and `Conduit.Contracts` must carry zero package references (a downstream domain project references it alone, precisely to avoid pulling in packages). This is deliberate, not an oversight — do not "fix" it by moving `ConduitFile` into `Conduit.Contracts`.
- `Conduit.Handlers` — `ICommandHandler`, `IQueryHandler`, `IEventListener` (`src/Conduit/Handlers/`)
- `Conduit.Attributes` — `LocalOnlyAttribute`, `ClientVisibleAttribute`, `RemoteContractsAttribute`, `ClosedSetAttribute`, `AuditRedactedAttribute`, `AuditAsAttribute` (`src/Conduit.Contracts/Attributes/`)
- `Conduit.Exceptions` — `ConduitException`, `NotFoundException`, `UnauthorizedException`, `ConflictException`, `PreconditionFailedException` (`src/Conduit.Contracts/Exceptions/`)
- `Conduit` (root) — `IMediator`, `IWireMediator` (`src/Conduit/`)

**Key design decisions:**
- **Auto-discovery**: The source generator discovers ALL types implementing `ICommand`, `IQuery`, or `IEvent` across the entire compilation — including referenced assemblies. This is intentional: contract types defined in a shared library (e.g., `MyApp.Contracts`) are discovered when referenced by the handler project. Types from NuGet packages may also be discovered; use `[RemoteContracts("Ns")]` to suppress CONDUIT001 for types without local handlers. Abstract and inaccessible types are excluded automatically; a generic message type (CONDUIT007) and a full type name two assemblies declare (CONDUIT008) are build errors. Dispatch is by exact type: the in-process switches match the runtime type's full name, as the wire does.
- **Runtime routing (commands/queries only)**: Routing is configured per-endpoint via `endpoint.RouteNamespace<T>()` (all types in a namespace) or `endpoint.RouteType<T>()` (single type). Events don't route — they broadcast. Unrouted messages dispatch locally.
- **Endpoint-based routing**: Messages route to named endpoints via `ConduitBuilder.AddEndpoint()`. Endpoints are just names — the transport is registered as a keyed `IConduitTransport` singleton in DI (key = endpoint name) and resolved at dispatch time via `ConduitRuntime.ResolveTransport()`. Routes define WHERE command/query types go. Transport extensions call `builder.UseTransport<T>()` which registers the keyed DI forwarding.
- **Per-endpoint transport registration**: every endpoint's options and transport are keyed singletons under the endpoint name, built via `ActivatorUtilities.CreateInstance` so only the options are passed explicitly. No transport package registers a non-keyed options or concrete-transport instance — two endpoints on one transport package are two independent instances, never a shared last-write-wins singleton. The non-keyed `IConduitTransport`/`IHostedService` entries are one-per-endpoint forwarders to the keyed instance.
- **Configuration binding**: a source-generated `AddConduit(IConfiguration, Action<ConduitBuilder>?)` overload binds `ConduitOptions` scalars from the `Conduit` section and hands each endpoint its `Conduit:Endpoints:{endpointName}` section, which its `Use*` extension binds onto the transport's options via `ConduitConfigurationBinder`. Configuration binds first and the `configure` callback runs second, so code always wins; named pools merge across both sources. Endpoints, routes, pipelines and type filters stay code-declared. See [`docs/configuration-reference.md`](docs/configuration-reference.md#loading-from-iconfiguration).
- **Topic-based message flow (broker transports)**: All messages (commands, queries, events) flow through the topic/exchange. Subscriptions route messages from the topic to the appropriate queue. Reply messages are the only exception — they bypass the topic and go point-to-point to the reply queue.
- **Topology modes**: `TopologyMode` enum (Production/Developer/Test) on `ConduitOptions` controls resource naming and durability. `SystemName` (default `"conduit"`) replaces per-transport topic/exchange name configuration. `TopologyNaming` static class in core generates all resource names consistently across transports, using `TopologyPrefix` which auto-resolves on first access (Developer defaults to `Environment.UserName`, Test generates a random 13-char prefix, Production resolves to null). Ephemeral resources: Developer mode = 4h auto-delete, Test mode = 5min auto-delete.
- **Background jobs**: `conduit.AddJobs(...)` declares cron/interval schedules and event triggers whose bodies are ordinary commands. Durability is inferred, not declared — a persistence engine means every schedule fires exactly once cluster-wide, no engine means every schedule fires on every host. Coordination reuses the outbox's atomic claim SQL by pointing `IClaimableTableSchema.CreatedAtColumn` at the run's due time, so there is no new per-engine SQL. Occurrences are computed in memory from the declaration (every host agrees without coordinating); the database only decides who runs one. `AddJobs` must come **after** the engine registration. Event triggers can coalesce: `CoalesceBy`/`Merge` accumulate a burst in `conduit_job_scopes` and materialize one run per real execution, so a job that internally deduplicates stops being misreported as N runs. A scope's run key is the coalesce key itself, so `JobKey`'s uniqueness bounds a key to one run in flight and one scope accumulating behind it — and coalescing adds no per-engine SQL either, reusing the same outbox-derived materialize and claim. See [`docs/jobs.md`](docs/jobs.md).
- **TypeFilterInterceptor**: Access control interceptor in core that rejects every inbound message (commands, queries **and events**) whose type is not in the allowed set, with `ForbiddenException` (403). Configured via `conduit.AddTypeFilter(filter => filter.AllowType(...))`. Transport-agnostic — works with any inbound path. A refused event reaches the publisher: `HandleWireEvent` logs it at Warning (EventId 7 `RefusedEvent`, no stack) and rethrows; HTTP `/publish` answers the `DispatchResult` envelope status-coded as `/dispatch` does, the hub's `Publish` throws `HubException(message)`, and a broker transport acknowledges it. An unknown wire type is a refusal too (`ConduitRuntime.UnknownMessageType`: `BadRequest`, Warning EventId 8, no reference), and a cancellation of the caller's token propagates from `HandleWireRequest`/`HandleWireEvent` unlogged.
- `IWireMediator : IMediator` — extended interface for inbound wire dispatch. Adds `HandleWireRequestAsync` and `HandleWireEventAsync`. Transport packages inject this; application code uses `IMediator`.
- `IWireExceptionTranslator` (`Conduit.Runtime`) — runtime extension point: `ConduitException? Translate(Exception)`. `HandleWireRequest` consults every registered translator in its catch-all before answering `InternalError`; a claimed exception becomes that refusal (no support reference), a throwing translator is logged and skipped. The generated mediator hands `HandleWireRequest` its `IServiceProvider`, and the runtime resolves the translators from it. Typical use: a host maps its data layer's concurrency exception to `PreconditionFailedException` (412).
- **A refusal in the reader's language**: a throw site names its sentence with `RefusalText` (`ConduitException.Text`, `DispatchErrorDetail.Text`; a value that is a word is a `TextName`), never on the wire. The host registers an `IDispatchCultureResolver` (reads the reader's culture from the dispatch's `MessageContext`; `DispatchPropertyCultureResolver` is the ready-made one) and an `IRefusalTextRenderer` (owns the catalog; null keeps the English). Conduit writes the sentence once, at the three edges where an answer leaves the server for a person: the envelope endpoint and the hub (`RefusalRendering.RenderResult`: message and each detail) and REST (`ConduitRestHandlers.ToProblem`: `detail` only, through `RefusalRendering.RenderMessage`). In process, `HandleWireRequestAsync` and `PreflightWireRequestAsync` answer English with `DispatchError.Text`. English, byte-identical, whenever either is absent, declines or fails; Conduit never sets `CurrentCulture`. See [`docs/error-handling.md`](docs/error-handling.md#a-refusal-in-the-readers-language).
- **Three-layer middleware model** for inbound wire messages:
  - `IInboundInterceptor` (gate) — pre-deserialization, receives only `string messageType`, for requests and events alike. Returns `bool` to allow/discard (`false` is a silent discard); a thrown `ConduitException` is a refusal answered to the caller, for an event as for a request. Runs before payload deserialization. Used by `TypeFilterInterceptor` (access control, throws) and `InboxInterceptor` (dedup, returns false). `MessageContext.Current` is available for accessing the transport-provided MessageId.
  - `IPipelineBehavior<TRequest, TResult>` — post-deserialization, wraps the strongly-typed handler with `next()` chain. Used by resilience, outbox, validation.
  - `IOutboundInterceptor` — intercepts outbound F&F sends. When present, the generated mediator calls the interceptor *instead of* sending to the transport. Used by outbox capture.
- `IConduitTransport` is a bidirectional transport with `SendRequestAsync` (request/response) and `SendEventAsync` (fire-and-forget events), both envelope-based.
- Generated `ConduitMediator` implements `IWireMediator`, checks route table for commands/queries before local dispatch. Registered as **transient** (stateless — resolves handlers from `IServiceProvider` per call). Handlers are also transient. This allows `IMediator` to be injected into singletons like `BackgroundService` without scope issues.
- **Unified pipeline behaviors**: `IPipelineBehavior<TRequest, TResult>` wraps commands, queries, and events uniformly. Register open generics via `conduit.AddPipeline(typeof(MyPipeline<,>))`. Order is explicit (first registered = outermost). Pipeline types are stored in `ConduitOptions.PipelineTypes` and resolved via `ConduitRuntime.ResolvePipelines` — ordering is owned by Conduit, not DI.
- **Event listener resolution**: the generated `PublishLocalAsync` emits a dispatch case for **every** discovered event type, not only for types with a listener found at compile time, and each case resolves its listeners from DI at publish time. That is what lets a listener registered at runtime (e.g. `Conduit.Jobs`' `EventTriggerListener<TEvent>`) receive events. It also means pipeline behaviors run for events with no listeners.
- **Event broadcast model**: `PublishAsync` dispatches to local listeners AND broadcasts to all DI-registered `IConduitTransport` instances (server-mode transports). Events marked `[LocalOnly]` skip transport broadcast: the generated gate lists the discovered events that are not `[LocalOnly]` and fails closed, so an event type the generator never saw is not broadcast either. Wire-inbound events use `PublishLocalAsync` (private) to prevent re-broadcast loops.
- **Event error propagation**: Listener failures use continue-on-failure semantics — all listeners run even if some throw. `PublishLocalAsync` collects exceptions and throws `EventListenerException` if any failed. `PublishAsync` (inline local path) and `DispatchEventAsync` (wire path) let it propagate — to the publisher, and so transports can retry/dead-letter; `InnerException` is the first listener failure. Only a `[FireAndForget]` event's background listeners catch and log it. Generated code emits inline try/catch per listener (no runtime helper wrapper).
- **Transport limitations**: HTTP has no server→client push — events only flow client→server over HTTP. SignalR supports bidirectional event flow. Cross-pod unsolicited events require a SignalR backplane.
- **Unified wire format**: All transports send the plain serialized message as the body with the type discriminator (`FullTypeName`, e.g. `"Orders.Commands.CreateOrder"`) in transport-native properties. HTTP uses the `X-Conduit-Type` header, SignalR uses a hub method parameter, broker transports use native message properties (AMQP `Type`/`Subject`, etc.). `DispatchEnvelope` is the internal handoff type — it is not exposed on the wire.
- **Browser MessageContext (caller ids are opt-in per edge)**: Browser clients (TypeScript) are always conversation origins, enforced on both sides. The TS `HttpTransport` drops caller `X-Conduit-Message-Id`/`-Conversation-Id`/`-Causation-Id` headers (any case) and `SignalRTransport` passes a null hub context. The HTTP wire endpoint and `ConduitHub` ignore caller ids and the server mints (a self-causating head) unless the edge opts in — `MapConduitEndpoint(basePath, trustCallerContext: true)`, `AddConduitSignalR(o => o.TrustCallerContext = true)` — and even then honor them only when message and conversation ids are both present and every supplied id passes `MessageContextIds.IsValid` (`Conduit.Runtime`, `^[A-Za-z0-9-]{1,64}$` over the whole string); one bad id discards the set, and the request is still answered normally. REST endpoints and `WithConduitDispatchContext()` routes never trust. Why: the inbox dedups on `MessageId` and audit lineage follows the other two. Enrichers apply either way. C# `HttpTransport`/`SignalRTransport` send their context whenever all three ids pass `IsValid` (the receiving edge decides) and none otherwise (a broker can deliver a foreign producer's ids; the receiver then mints); broker transports always propagate it via native properties.
- **ErrorCode enumeration class**: `ConduitException.Code` is an `ErrorCode` record that co-locates the machine-readable code string with its HTTP status code. Equality is based on `Value` only. RESTful endpoints use `Code.HttpStatusCode` directly for Problem Details responses. The envelope endpoint uses `ErrorCode.ToHttpStatusCode()` to map the string-based `DispatchError.Code` from the wire format.
- **DispatchResult invariants**: `DispatchResult<T>` uses factory methods `Ok(data)` and `Fail(code, message, details?)` with a `[JsonConstructor]` private constructor for deserialization. An unexpected failure answers `InternalError` with the fixed message `"An unexpected error occurred."` and its support reference in `DispatchError.Reference` (never in the message); `ConduitException.Reference` forwards through `Fail(ConduitException)`.
- **TypeScript generation**: Types marked `[ClientVisible]` get TypeScript interfaces emitted by `Conduit.TypeGen`. This includes both dispatchable types and non-dispatchable DTOs/models. TypeGen emits a self-contained runtime (`conduit.ts`, assembled from the real TypeScript in `src/Conduit.TypeGen/Transport/` — see that project's CLAUDE.md) with transports and a per-project file (`conduit.generated.ts`) with `ConduitClient` — no npm package needed. `ConduitClient` is a thin dispatch client (not a mediator) with `execute`/`fetch`/`publish`/`on` methods. Enums emit as unions of their member names (a member's `[JsonStringEnumMemberName]` when it has one; a `[Flags]` enum emits `string`, since a combination is written `"Read, Write"`); a closed set (`IClosedSet<TSelf>` + `[ClosedSet]`) as a union of its values — the wire carries each in that spelling (`ConduitRuntime.JsonOptions`).
- **MessageContext**: AsyncLocal-backed context tracking `MessageId`, `CausationId`, `ConversationId` through dispatch chains. Set automatically by the generated mediator. A head/origin dispatch self-causates: `CausationId == MessageId == ConversationId`. `CausationId` is a non-nullable `string` and is never null. Nested dispatches inherit `ConversationId` and set `CausationId` to the parent's `MessageId`. Access via `MessageContext.Current`. `StartDispatch()` for local dispatch, `StartDispatchFromTransport(messageId, causationId, conversationId)` for inbound wire messages — a wire message that omits `causationId` defaults it to `messageId` (self-causation). `StartLocalDelivery()` for the outbox's local delivery: a child whose `DeliveredMessageId` is the outbox entry's `MessageId` (`DeliveredMessageId` otherwise equals `MessageId`); a non-coalescing trigger keys its run on it. `Properties` names are case-insensitive (`OrdinalIgnoreCase`) whoever built the bag — the private constructor copies any other bag — so a header name HTTP/2 lower-cased still matches, and an `IDispatchContextEnricher` that owns a key must write or remove it on every dispatch (a no-op keeps the client's value). The HTTP wire endpoint reads client properties (`X-Conduit-Property-*`) only when mapped with `readClientProperties: true`; the hub always reads its `headers`.
- **BrokerTransportBase<TMessage>**: Template method base class for all 4 broker transports. Owns `IHostedService` lifecycle, inbound dispatch orchestration (`ProcessInboundMessageAsync`), outbound serialization. Transports implement ~15 abstract members for broker-specific primitives.
- **Unified broker options** (`Conduit.Runtime`): all 4 broker options classes inherit `BrokerTransportOptions`, which owns the default concurrency knob (`DefaultConcurrency`), the `Pools` collection, and `AddPool(...)`. Named pools use a single shared `ReceiverPoolOptions` (`Name`, `Concurrency`, `MessageTypes` via `Handle(fullTypeName)`). `Concurrency` is one `int` interpreted per broker: prefetch count for RabbitMQ/ActiveMQ (cast to `ushort` for RabbitMQ at the consumption site), max-concurrent-calls for Azure Service Bus, polling parallelism for Amazon SQS. Each transport's options class adds only broker-specific extras (connection settings, region/credentials, `AdminConnectionString`, `ResponseTimeout`, `EndpointName`, etc.).
- **TopologyPrefix**: `ConduitOptions.TopologyPrefix` is a single settable property that auto-resolves on first access: Developer mode defaults to `Environment.UserName`, Test mode generates a random 13-char prefix, Production mode resolves to null. Can be explicitly set for multi-host integration tests (all hosts share the same prefix).
- **Configurable ResponseTimeout**: Each broker transport options class has a `ResponseTimeout` property (default 60s). Configurable per-transport.
- **ProducerOnly mode**: `ConduitOptions.ProducerOnly` makes a broker transport host only publish messages. Skips inbound topology (subscriptions, bindings) and consumers. Reply queues for request/response still created via `EnsureOutboundTopologyAsync`. Use for API hosts that route commands to workers, or test sender hosts.
- **No application retry in transports**: Broker transports are one-shot dispatch + dead letter on failure. Application retry is the user's concern via `IPipelineBehavior<,>` + Polly.
- **Fire-and-forget dispatch**: `ConduitBackgroundDispatcher` is a `BackgroundService` with an unbounded `Channel<T>` that executes F&F work items sequentially. For local F&F void commands, the generated `ConduitDispatch.Execute` enqueues the handler to the background dispatcher. For wire-inbound F&F commands, `ConduitDispatch.ExecuteVoidWire` runs the handler inline within the DI scope (the transport already provides F&F semantics). For F&F events, local listeners are enqueued to the background dispatcher but transport broadcast proceeds inline. Pipeline behaviors run in the caller context for all F&F paths. On host shutdown, items already queued run to completion with a live token until the host's shutdown deadline. At the deadline the running item's token is cancelled, `StopAsync` returns even if that item ignores it, and the items still queued are logged (`ShutdownAbandoned`, event 303), not run.

## Attributes

| Attribute | Scope | Purpose |
|---|---|---|
| `[LocalOnly]` | Type | Prevent event from being broadcast to transports. Inherited: an event deriving from a `[LocalOnly]` event is local-only too. |
| `[FireAndForget]` | Type | Void commands: local handler runs in background, routed dispatch uses `SendEventAsync` (no reply). Events: local listeners run in background (transport broadcast unchanged — events are already fire-and-forget on the wire). Invalid on `ICommand<TResult>` or `IQuery<TResult>` (CONDUIT003). Inherited by a derived message, as `[LocalOnly]` is. |
| `[ClientVisible]` | Type | Generate TypeScript for this type |
| `[ClosedSet("a", …)]` | Type | State a closed set's wire values for TypeGen (the type implements `IClosedSet<TSelf>`; the list must equal `All`, checked at runtime) |
| `[DoNotAudit]` | Type, property, record parameter | Type: suppress payload capture in audit trail (observations still emitted). Member: capture the body without that member (`ConduitRuntime.AuditJsonOptions`; wire serialization unaffected). Wire-inbound dispatch captures the same redacted body (`ConduitRuntime.CaptureAuditPayload`); a route-forwarded request records none. A member typed `JsonElement`/`JsonNode`/`object` (or `[JsonExtensionData]`) is captured verbatim -- the marks cannot reach inside it -- unless it is `[AuditAs]`, `[DoNotAudit]` or `[AuditRedacted]`; otherwise suppress the type's payload. |
| `[AuditRedacted]` | Property, record parameter | Capture that the member has a value (`"•••"`), never the value; null/"" as themselves. Not combinable with [DoNotAudit] (CONDUIT005). |
| `[AuditAs(nameof(Type))]` | Property, record parameter | On a `JsonElement`/`JsonElement?` member holding a serialized message whose wire type name is in the named sibling `string` property: capture it as that message, redacted by its own marks. Resolves only registered message types (`ConduitTypeRegistry`); inner type that records no body (type-level `[DoNotAudit]`, or the host's `Suppress<T>()`/`SuppressPayload<T>()`, process-wide) -> member left out; null -> null; unknown/null name or unreadable JSON -> `"•••"`, never the raw JSON. Misapplied on a member of a message, or of a type a message carries (in the compilation or a referenced assembly that references `Conduit.Contracts`): CONDUIT006. |
| `[NotAutoRegistered]` | Type | Exclude a handler/listener from source-gen auto-registration (for components wired explicitly via a feature's `Add*` extension, e.g. `LocalAuditListener` via `AddAuditListener`). The type stays dispatchable when registered manually. |
| `[RemoteContracts("Ns")]` | Assembly | Suppress CONDUIT001 for types in namespace and its sub-namespaces, on whole segments (remote contract types without local handlers) |

## Build & Test

```bash
dotnet build          # builds everything including sample apps
dotnet test           # runs all tests across 14 test projects
(cd tests/transport-ts && npm ci && npm test)   # the TS transport: strict tsc + vitest (CI job transport-ts)
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
- Shared test infrastructure in `tests/Conduit.TestUtils/` (`TestHost`, `EventSpy<T>`, `SpyLog`, `SpyLoggerProvider`, `ContainerHelper`, `MessageContextSnapshot`, `TestTopologyPrefix`)
- `TestTopologyPrefix` builds a per-project Test topology prefix `test-{hash8(assembly)}-{random}` (`ProjectHash`/`ProjectPrefix`/`NewForTest`). All hosts in a test project share the stable `test-{hash8}-` segment so the project can clean up exactly the resources it created.
- Broker transport tests use per-transport abstract base classes (`RabbitMqTestBase`, `AsbTestBase`, etc.) that own container lifecycle (static), factory methods, and `[TestInitialize]` availability guard. `AssemblySetup` delegates to base class static init/cleanup methods.
- Ephemeral resource cleanup for the TTL-less broker: the SQS test project reaps its own `test-{hash8}-` prefix once at `[AssemblyInitialize]` (`*TestBase.ReapProjectResourcesAsync()` → `AmazonSqsTopologyReaper.ReapByPrefixAsync`). RabbitMQ/ASB/ActiveMQ self-clean via native auto-delete/TTL, so they don't reap. Cleanup is once-per-project (NOT per-host-startup — that cross-deletes concurrent hosts).
- All test hosts must use `await using` — this is the sole disposal mechanism (no host tracking or `[TestCleanup]` disposal loops). `TestHost.DisposeAsync` is idempotent and disposes the underlying host via `IAsyncDisposable.DisposeAsync` (not sync `Dispose()`) — broker transports implement `IAsyncDisposable` only, and sync disposal deadlocks AmqpNetLite's link close in ActiveMQ.
- Transport test matrices documented in `docs/transport-test-matrices.md` — defines the required tests for every broker transport (behavior tests + universal topology tests + transport-specific tests + container-free per-endpoint registration and configuration-binding tests). New transports MUST implement the full matrix.

## Project-Specific Notes

Each project has its own `CLAUDE.md` with implementation-level detail. These are auto-loaded when Claude touches files in that directory — read them directly when working on that subsystem:

- [`src/Conduit/CLAUDE.md`](src/Conduit/CLAUDE.md) — core runtime types (`ConduitRuntime`, `TransportDispatcher`, `BrokerTransportBase`, `ConduitTypeRegistry`), configuration, exceptions
- [`src/Conduit.Analysis/CLAUDE.md`](src/Conduit.Analysis/CLAUDE.md) — shared type model (netstandard2.0)
- [`src/Conduit.SourceGen/CLAUDE.md`](src/Conduit.SourceGen/CLAUDE.md) — Roslyn source generator internals, emitted files, attribute handling
- [`src/Conduit.TypeGen/CLAUDE.md`](src/Conduit.TypeGen/CLAUDE.md) — MSBuild task, MetadataLoadContext scanning, TypeScript emission
- [`src/Conduit.Persistence/CLAUDE.md`](src/Conduit.Persistence/CLAUDE.md) — engine-agnostic relational persistence abstraction (`ISqlDialect`, connection factories, `DbStoreBase`) + the four engine packages (Sqlite/Postgres/SqlServer/MySql)
- [`src/Conduit.Durability/CLAUDE.md`](src/Conduit.Durability/CLAUDE.md) — outbox + inbox + dead-letter queue implementation, dialects, configuration
- [`src/Conduit.Audit/CLAUDE.md`](src/Conduit.Audit/CLAUDE.md) — audit observer, consolidation, AuditBatch event, local store
- [`src/Conduit.Jobs/CLAUDE.md`](src/Conduit.Jobs/CLAUDE.md) — schedules, event triggers, the run ledger and its reuse of the outbox claim, claim renewal
- [`src/Conduit.Resilience/CLAUDE.md`](src/Conduit.Resilience/CLAUDE.md) — Polly v8 resilience pipeline behavior, named pipelines, predicates, context enrichment
- [`src/Conduit.Transport.Http/CLAUDE.md`](src/Conduit.Transport.Http/CLAUDE.md)
- [`src/Conduit.Transport.SignalR/CLAUDE.md`](src/Conduit.Transport.SignalR/CLAUDE.md) — dual-mode server/client transport
- [`src/Conduit.Transport.AzureServiceBus/CLAUDE.md`](src/Conduit.Transport.AzureServiceBus/CLAUDE.md) — includes emulator test caveats
- [`src/Conduit.Transport.RabbitMQ/CLAUDE.md`](src/Conduit.Transport.RabbitMQ/CLAUDE.md)
- [`src/Conduit.Transport.AmazonSqs/CLAUDE.md`](src/Conduit.Transport.AmazonSqs/CLAUDE.md)
- [`src/Conduit.Transport.ActiveMQ/CLAUDE.md`](src/Conduit.Transport.ActiveMQ/CLAUDE.md)

### User & conceptual docs

Prose guides under `docs/` for understanding the system without reading source. Start here when
onboarding or answering "how does X work" questions:

- [`docs/dispatch-flows.md`](docs/dispatch-flows.md) — end-to-end traces (local dispatch, event broadcast, wire-inbound) with real type/method names
- [`docs/glossary.md`](docs/glossary.md) — domain vocabulary (endpoint, route, topology, gate, interceptor, envelope, MessageContext, reply queue, ...)
- [`docs/diagnostics.md`](docs/diagnostics.md) — CONDUIT001–CONDUIT008 source-generator diagnostics and how to resolve/suppress them
- [`docs/getting-started.md`](docs/getting-started.md) — first-run walkthrough
- [`docs/core-concepts.md`](docs/core-concepts.md) — messages, handlers, mediator, the mental model
- [`docs/configuration-reference.md`](docs/configuration-reference.md) — `ConduitOptions` / builder reference
- [`docs/pipeline-behaviors.md`](docs/pipeline-behaviors.md) — `IPipelineBehavior<,>` ordering and usage
- [`docs/dispatch-retry.md`](docs/dispatch-retry.md) — `IDispatchRetryPolicy`, fresh scope per attempt, nested vs independent dispatches, the retry counter
- [`docs/error-handling.md`](docs/error-handling.md) — `ConduitException`, `ErrorCode`, Problem Details
- [`docs/modular-monolith.md`](docs/modular-monolith.md) — handler composition across modules in one process
- [`docs/persistence.md`](docs/persistence.md) — choosing an engine, the tables each durable feature creates, one database for all of them
- [`docs/audit.md`](docs/audit.md) — audit trail / observer subsystem
- [`docs/jobs.md`](docs/jobs.md) — background jobs: schedules, event triggers, durability, cancellation
- [`docs/outbox.md`](docs/outbox.md) — transactional outbox (`IOutboundInterceptor`)
- [`docs/inbox.md`](docs/inbox.md) — idempotent inbox dedup (`InboxInterceptor` gate)
- [`docs/dead-letter-queue.md`](docs/dead-letter-queue.md) — DLQ capture and replay
- [`docs/security.md`](docs/security.md) — access control, `TypeFilterInterceptor`, auth model
- [`docs/faq.md`](docs/faq.md) — frequently asked questions
- [`docs/transports/index.md`](docs/transports/index.md) — transport overview and routing. Every
  transport has a user guide alongside it: `signalr.md`, `http.md`, `typescript-client.md`,
  `azure-service-bus.md`, `rabbitmq.md`, `amazon-sqs.md`, `activemq.md`. User-facing
  facts (setup, options, configuration keys, topology, wire format) belong in those guides; the
  per-project `CLAUDE.md` files keep only what a maintainer of the code needs.

## Sample Apps

- `samples/browser-client/Conduit.Sample.Notes/` — browser-facing single service with SignalR hub, notes CRUD, events pushed to browser. Vite + TypeScript frontend in `ClientApp/` consuming generated `ConduitClient` + `SignalRTransport`
- `samples/service-to-service/Conduit.Sample.Orders/` — order service that routes commands to InventoryService via SignalR, exposes HTTP dispatch
- `samples/service-to-service/Conduit.Sample.Inventory/` — internal inventory service handling commands via SignalR hub
- `samples/rest-api/Conduit.Sample.Todos/` — REST API demonstrating `MapConduitGet/Post/Put/Delete` with OpenAPI/Swagger UI

## Design Spec

Full design specification at `docs/design.md` — covers interfaces, generated code patterns, TypeScript output structure, validation strategy, auth model, and future Rebus integration plans.
