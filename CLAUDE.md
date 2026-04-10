# Conduit

Source-generated mediator library for .NET 10 with HTTP/SignalR transport, TypeScript codegen, and unified event transport. Replaces MediatR with a reflection-free, compile-time approach.

## Architecture

Thirteen source projects, twelve test projects, one shared test utility, four sample apps:

```
src/Conduit/                        Core interfaces, routing, dispatch pipeline, configuration — net10.0
src/Conduit.Analysis/               Shared type model (DispatchableTypeInfo, etc.) — netstandard2.0 (PolySharp)
src/Conduit.SourceGen/              Roslyn incremental source generator + RoslynScanner — netstandard2.0 (PolySharp)
src/Conduit.TypeGen/                MSBuild task for TypeScript emission + ReflectionScanner — net10.0

src/Conduit.Transport.Http/              HTTP transport (outbound HttpTransport + inbound MapConduitEndpoint) — net10.0
src/Conduit.Transport.SignalR/           SignalR transport (outbound SignalRTransport + inbound ConduitHub) — net10.0
src/Conduit.Transport.AzureServiceBus/   Azure Service Bus transport (IConduitTransport + IHostedService) — net10.0
src/Conduit.Transport.RabbitMQ/          RabbitMQ transport (IConduitTransport + IHostedService) — net10.0
src/Conduit.Transport.Kafka/             Kafka transport (IConduitTransport + IHostedService) — net10.0
src/Conduit.Transport.AmazonSqs/         Amazon SQS (+SNS) transport (IConduitTransport + IHostedService) — net10.0
src/Conduit.Transport.ActiveMQ/          ActiveMQ Artemis transport (IConduitTransport + IHostedService) — net10.0
src/Conduit.Durability/                 Durable messaging — transactional outbox + inbox (idempotent dedup) — net10.0
```

**Dependency graph:**
- `Transport.Http` → `Conduit` (core) + ASP.NET Core
- `Transport.SignalR` → `Conduit` (core) + ASP.NET Core + SignalR
- `Transport.AzureServiceBus` → `Conduit` (core) + Azure.Messaging.ServiceBus
- `Transport.RabbitMQ` → `Conduit` (core) + RabbitMQ.Client
- `Transport.Kafka` → `Conduit` (core) + Confluent.Kafka
- `Transport.AmazonSqs` → `Conduit` (core) + AWSSDK.SimpleNotificationService + AWSSDK.SQS
- `Transport.ActiveMQ` → `Conduit` (core) + AMQPNetLite.Core
- `Durability` → `Conduit` (core) + Microsoft.Data.Sqlite (built-in dialect)
- `SourceGen` → `Analysis` → Roslyn APIs
- `TypeGen` → `Analysis`
- Core has no ASP.NET dependency. Depends only on DI abstractions, Hosting abstractions, Logging abstractions, and Options.

**Transport packages contain both sides:**
- Each transport package has an **outbound** client (`HttpTransport`, `SignalRTransport`) and an **inbound** endpoint (`MapConduitEndpoint()`, `ConduitHub`). There is no separate "bridge" concept — browser clients and C# clients use the same transport infrastructure.

**Namespace layout** (within `src/Conduit/`):
- `Conduit.Configuration` — builder, options, endpoints, routes, `TopologyMode` (`src/Conduit/Configuration/`)
- `Conduit.Runtime` — wire types + runtime: `ConduitRuntime`, `TransportDispatcher`, `DispatchEnvelope`, `DispatchResult`, `IConduitTransport`, `MessageContext`, `TopologyNaming`, `ConduitTypeRegistry` (`src/Conduit/Runtime/`)
- `Conduit.Middleware` — pipeline, gate, and interceptor types: `IPipelineBehavior`, `IInboundInterceptor` (gate), `IOutboundInterceptor`, `TypeFilterInterceptor` (`src/Conduit/Middleware/`)
- `Conduit.Messages` — `ICommand`, `IQuery`, `IEvent`, `Unit` (`src/Conduit/Messages/`)
- `Conduit.Handlers` — `ICommandHandler`, `IQueryHandler`, `IEventListener` (`src/Conduit/Handlers/`)
- `Conduit.Attributes` — `LocalOnlyAttribute`, `ClientVisibleAttribute`, `RemoteContractsAttribute` (`src/Conduit/Attributes/`)
- `Conduit.Exceptions` — `ConduitException`, `NotFoundException`, `UnauthorizedException`, `ConflictException` (`src/Conduit/Exceptions/`)
- `Conduit` (root) — `IMediator`, `IWireMediator` (`src/Conduit/`)

**Key design decisions:**
- **Auto-discovery**: The source generator discovers ALL types implementing `ICommand`, `IQuery`, or `IEvent` across the entire compilation — including referenced assemblies. This is intentional: contract types defined in a shared library (e.g., `MyApp.Contracts`) are discovered when referenced by the handler project. Types from NuGet packages may also be discovered; use `[RemoteContracts("Ns")]` to suppress CONDUIT001 for types without local handlers. Abstract and inaccessible types are excluded automatically.
- **Runtime routing (commands/queries only)**: Routing is configured per-endpoint via `endpoint.RouteNamespace<T>()` (all types in a namespace) or `endpoint.RouteType<T>()` (single type). Events don't route — they broadcast. Unrouted messages dispatch locally.
- **Endpoint-based routing**: Messages route to named endpoints via `ConduitBuilder.AddEndpoint()`. Endpoints are just names — the transport is registered as a keyed `IConduitTransport` singleton in DI (key = endpoint name) and resolved at dispatch time via `ConduitRuntime.ResolveTransport()`. Routes define WHERE command/query types go. Transport extensions call `builder.UseTransport<T>()` which registers the keyed DI forwarding.
- **Topic-based message flow (broker transports)**: All messages (commands, queries, events) flow through the topic/exchange. Subscriptions route messages from the topic to the appropriate queue. Reply messages are the only exception — they bypass the topic and go point-to-point to the reply queue.
- **Topology modes**: `TopologyMode` enum (Production/Developer/Test) on `ConduitOptions` controls resource naming and durability. `SystemName` (default `"conduit"`) replaces per-transport topic/exchange name configuration. `TopologyNaming` static class in core generates all resource names consistently across transports, using `TopologyPrefix` which auto-resolves on first access (Developer defaults to `Environment.UserName`, Test generates a random 13-char prefix, Production resolves to null). Ephemeral resources: Developer mode = 4h auto-delete, Test mode = 5min auto-delete.
- **TypeFilterInterceptor**: Access control interceptor in core that rejects dispatch requests for types not in the allowed set. Configured via `conduit.AddTypeFilter(filter => filter.AllowType(...))`. Transport-agnostic — works with any inbound path.
- `IWireMediator : IMediator` — extended interface for inbound wire dispatch. Adds `HandleWireRequestAsync` and `HandleWireEventAsync`. Transport packages inject this; application code uses `IMediator`.
- **Three-layer middleware model** for inbound wire messages:
  - `IInboundInterceptor` (gate) — pre-deserialization, receives only `string messageType`. Returns `bool` to allow/reject. Runs before payload deserialization. Used by `TypeFilterInterceptor` (access control) and `InboxInterceptor` (dedup). `MessageContext.Current` is available for accessing the transport-provided MessageId.
  - `IPipelineBehavior<TRequest, TResult>` — post-deserialization, wraps the strongly-typed handler with `next()` chain. Used by resilience, outbox, validation.
  - `IOutboundInterceptor` — intercepts outbound F&F sends. When present, the generated mediator calls the interceptor *instead of* sending to the transport. Used by outbox capture.
- `IConduitTransport` is a bidirectional transport with `SendRequestAsync` (request/response) and `SendEventAsync` (fire-and-forget events), both envelope-based.
- Generated `ConduitMediator` implements `IWireMediator`, checks route table for commands/queries before local dispatch. Registered as **transient** (stateless — resolves handlers from `IServiceProvider` per call). Handlers are also transient. This allows `IMediator` to be injected into singletons like `BackgroundService` without scope issues.
- **Unified pipeline behaviors**: `IPipelineBehavior<TRequest, TResult>` wraps commands, queries, and events uniformly. Register open generics via `conduit.AddPipeline(typeof(MyPipeline<,>))`. Order is explicit (first registered = outermost). Pipeline types are stored in `ConduitOptions.PipelineTypes` and resolved via `ConduitRuntime.ResolvePipelines` — ordering is owned by Conduit, not DI.
- **Event broadcast model**: `PublishAsync` dispatches to local listeners AND broadcasts to all DI-registered `IConduitTransport` instances (server-mode transports). Events marked `[LocalOnly]` skip transport broadcast at compile time. Wire-inbound events use `PublishLocalAsync` (private) to prevent re-broadcast loops.
- **Event error propagation**: Listener failures use continue-on-failure semantics — all listeners run even if some throw. `PublishLocalAsync` collects exceptions and throws `EventListenerException` if any failed. `PublishAsync` (local path) catches and logs it (fire-and-forget). `DispatchEventAsync` (wire path) lets it propagate so transports can retry/dead-letter. Generated code emits inline try/catch per listener (no runtime helper wrapper).
- **Transport limitations**: HTTP has no server→client push — events only flow client→server over HTTP. SignalR supports bidirectional event flow. Cross-pod unsolicited events require a SignalR backplane.
- **Unified wire format**: All transports send the plain serialized message as the body with the type discriminator (`FullTypeName`, e.g. `"Orders.Commands.CreateOrder"`) in transport-native properties. HTTP uses the `X-Conduit-Type` header, SignalR uses a hub method parameter, broker transports use native message properties (AMQP `Type`/`Subject`, Kafka header, etc.). `DispatchEnvelope` is the internal handoff type — it is not exposed on the wire.
- **Browser MessageContext**: Browser clients (TypeScript) are always conversation origins. They do not send `MessageContext` — the server mints a fresh context on receipt. C# service-to-service transports propagate `MessageContext` automatically via transport-native properties/headers.
- **Dual-mode SignalRTransport**: Server mode (IHubContext, pushes events) and client mode (HubConnection, sends commands + receives events). Client mode uses lazy connect (auto-starts on first use via `EnsureConnectedAsync`).
- **ErrorCode enumeration class**: `ConduitException.Code` is an `ErrorCode` record that co-locates the machine-readable code string with its HTTP status code. Equality is based on `Value` only. RESTful endpoints use `Code.HttpStatusCode` directly for Problem Details responses. The envelope endpoint uses `ErrorCode.ToHttpStatusCode()` to map the string-based `DispatchError.Code` from the wire format.
- **DispatchResult invariants**: `DispatchResult<T>` uses factory methods `Ok(data)` and `Fail(code, message, details?)` with a `[JsonConstructor]` private constructor for deserialization.
- **TypeScript generation**: Types marked `[ClientVisible]` get TypeScript interfaces emitted by `Conduit.TypeGen`. This includes both dispatchable types and non-dispatchable DTOs/models. TypeGen emits a self-contained runtime (`conduit.ts`) with transports and a per-project file (`conduit.generated.ts`) with `ConduitClient` — no npm package needed. `ConduitClient` is a thin dispatch client (not a mediator) with `execute`/`fetch`/`publish`/`on` methods.
- **Wire dispatch route forwarding**: The generated `DispatchRequestAsync` checks routes before the local type switch. Unknown types matching a route are forwarded as raw envelopes to the transport without deserializing locally.
- **MessageContext**: AsyncLocal-backed context tracking `MessageId`, `CausationId`, `ConversationId` through dispatch chains. Set automatically by the generated mediator. Nested dispatches inherit `ConversationId` and set `CausationId` to the parent's `MessageId`. Access via `MessageContext.Current`. `StartDispatch()` for local dispatch, `StartDispatchFromTransport(messageId, causationId, conversationId)` for inbound wire messages.
- **BrokerTransportBase<TMessage>**: Template method base class for all 5 broker transports. Owns `IHostedService` lifecycle, inbound dispatch orchestration (`ProcessInboundMessageAsync`), outbound serialization. Transports implement ~15 abstract members for broker-specific primitives.
- **TopologyPrefix**: `ConduitOptions.TopologyPrefix` is a single settable property that auto-resolves on first access: Developer mode defaults to `Environment.UserName`, Test mode generates a random 13-char prefix, Production mode resolves to null. Can be explicitly set for multi-host integration tests (all hosts share the same prefix).
- **Configurable ResponseTimeout**: Each broker transport options class has a `ResponseTimeout` property (default 60s). Configurable per-transport.
- **ProducerOnly mode**: `ConduitOptions.ProducerOnly` makes a broker transport host only publish messages. Skips inbound topology (subscriptions, bindings) and consumers. Reply queues for request/response still created via `EnsureOutboundTopologyAsync`. Use for API hosts that route commands to workers, or test sender hosts.
- **No application retry in transports**: Broker transports are one-shot dispatch + dead letter on failure. Application retry is the user's concern via `IPipelineBehavior<,>` + Polly.
- **Fire-and-forget dispatch**: `ConduitBackgroundDispatcher` is a `BackgroundService` with an unbounded `Channel<T>` that executes F&F work items sequentially. For local F&F void commands, the generated `ConduitDispatch.Execute` enqueues the handler to the background dispatcher. For wire-inbound F&F commands, `ConduitDispatch.ExecuteVoidWire` runs the handler inline within the DI scope (the transport already provides F&F semantics). For F&F events, local listeners are enqueued to the background dispatcher but transport broadcast proceeds inline. Pipeline behaviors run in the caller context for all F&F paths.

## Attributes

| Attribute | Scope | Purpose |
|---|---|---|
| `[LocalOnly]` | Type | Prevent event from being broadcast to transports |
| `[FireAndForget]` | Type | Void commands: local handler runs in background, routed dispatch uses `SendEventAsync` (no reply). Events: local listeners run in background (transport broadcast unchanged — events are already fire-and-forget on the wire). Invalid on `ICommand<TResult>` or `IQuery<TResult>` (CONDUIT003). |
| `[ClientVisible]` | Type | Generate TypeScript for this type |
| `[RemoteContracts("Ns")]` | Assembly | Suppress CONDUIT001 for types in namespace (remote contract types without local handlers) |

## Build & Test

```bash
dotnet build          # builds everything including sample apps
dotnet test           # runs all tests across 11 test projects
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
- Shared test infrastructure in `tests/Conduit.TestUtils/` (`TestHost`, `EventSpy<T>`, `SpyLog`, `ContainerHelper`, `MessageContextSnapshot`)
- Broker transport tests use per-transport abstract base classes (`RabbitMqTestBase`, `AsbTestBase`, etc.) that own container lifecycle (static), factory methods, and `[TestInitialize]` availability guard. `AssemblySetup` delegates to base class static init/cleanup methods.
- All test hosts must use `await using` — this is the sole disposal mechanism (no host tracking or `[TestCleanup]` disposal loops). `TestHost.DisposeAsync` is idempotent and disposes the underlying host via `IAsyncDisposable.DisposeAsync` (not sync `Dispose()`) — broker transports implement `IAsyncDisposable` only, and sync disposal deadlocks AmqpNetLite's link close in ActiveMQ.
- Transport test matrices documented in `docs/transport-test-matrices.md` — defines the required tests for every broker transport (28 behavior tests + 16 universal topology tests + transport-specific tests). New transports MUST implement the full matrix.
- **Azure Service Bus emulator caveats**: The emulator has a hard cap of 50 queues+topics combined per namespace (not configurable — see [Azure/azure-service-bus-emulator-installer#48](https://github.com/Azure/azure-service-bus-emulator-installer/issues/48)). `AsbTestBase` tracks per-test topology prefixes via `AsyncLocal` and deletes topics + matching queues in `[TestCleanup]` to stay under the quota. `[assembly: DoNotParallelize]` forces sequential class execution for the same reason. If `Conduit:AzureServiceBus:ConnectionString` is set in user secrets (pointing at a real namespace), pass `Conduit__AzureServiceBus__ConnectionString=""` to force the emulator path.

## Project-Specific Notes

### Conduit (core)
- No ASP.NET dependency — only DI abstractions, Hosting abstractions, Logging abstractions, Options
- `ConduitRuntime` — static class centralizing route resolution, transport resolution (keyed DI), inbound gate chain, pipeline chaining, error handling. `HandleWireRequest`/`HandleWireEvent` run `IInboundInterceptor` gates before deserialization, then dispatch. Called primarily by source-generated mediator code.
- `TransportDispatcher` — singleton service encapsulating route resolution + transport dispatch. `SendAsync(envelope, ct)` resolves routes via `ConduitRuntime.ResolveRoute`, then sends via the endpoint's keyed transport (`SendRequestAsync`) or broadcasts to all transports (`SendEventAsync`). Registered by `AddConduitCore`. Used by `OutboxRelay` and `OutboxCapture.FlushAsync` instead of duplicating transport resolution logic.
- `TypeFilterInterceptor` (in `Conduit.Middleware`) — `IInboundInterceptor` gate that throws `UnauthorizedException` for types not in `TypeFilterOptions.AllowedTypes`. Configured via `conduit.AddTypeFilter()`.
- `AddConduit()` is generated by source gen. All configuration goes through `ConduitBuilder`: `AddEndpoint`, `AddPipeline`, `AddTypeFilter`. Routing is configured per-endpoint via `EndpointBuilder.RouteNamespace<T>()` and `EndpointBuilder.RouteType<T>()`.
- `TopologyMode` enum (Production/Developer/Test) and `SystemName`/`TopologyPrefix` properties on `ConduitOptions` — controls topology resource naming and durability across broker transports
- `TopologyNaming` static class — generates shared resource names (`CreateSharedResourceName`) and app-scoped resource names (`CreateAppResourceName`) with consistent prefix/truncation/hashing across all transports
- `ConduitTypeRegistry` — compile-time registry of event, command, and query message types. Properties: `EventTypes`, `CommandTypes`, `QueryTypes` (Type lists) and pre-computed `EventTypeNames`, `CommandTypeNames`, `QueryTypeNames` (string lists). Populated by source generator in `AddConduit()` with `typeof()` expressions. Broker transports resolve from DI for topology discovery instead of scanning `IServiceCollection` at runtime.
- `ApplicationName` on `ConduitOptions` defaults from `IHostEnvironment.ApplicationName` at first resolution via factory registration in `AddConduitCore`. No transport-side mutation needed.
- `ConduitBackgroundDispatcher` — `BackgroundService` with unbounded `Channel<T>` for F&F work items. Registered in DI by `AddConduitCore`. Sequential execution, linked cancellation for graceful shutdown.
- `BrokerTransportBase<TMessage>` — abstract base class for all 5 broker transports (ASB, RMQ, Kafka, SQS, ActiveMQ). Owns `IHostedService` lifecycle, inbound dispatch orchestration (context save/restore, scope creation, request/event branching, error handling), and outbound send (serialization, reply unwrap via `ConduitRuntime.UnwrapReply`). Transports implement ~15 abstract methods for transport-specific primitives. Uses `BinaryData` for payload passing. `StartAsync` branches on `ProducerOnly`: true → `ConnectAsync` + `EnsureOutboundTopologyAsync`; false → `ConnectAsync` + `EnsureTopologyAsync` + `StartConsumersAsync`. `EnsureOutboundTopologyAsync` is an abstract method for producer-only topology (reply queue + topic/exchange only).

### Conduit.Durability
Durable messaging infrastructure — inbox (inbound deduplication) and outbox (outbound capture + relay). Configured via `conduit.AddDurability()` with one shared database for all features.

**Directory layout:**
- `Configuration/` — `DurabilityBuilder`, `OutboxOptions`, `InboxOptions`
- `Persistence/` — `IDurabilityConnectionFactory`, `ConnectionStringFactory<T>` (internal), `IDurabilityDialect`
- `Persistence/Dialects/` — one class per database implementing `IDurabilityDialect` (SQLite, SQL Server, PostgreSQL, MySQL)
- `Persistence/Outbox/` — `IOutboxStore`, `DbConnectionOutboxStore`, `OutboxEntry`, `OutboxEntryStatus`, `ClaimedOutboxEntry`
- `Persistence/Inbox/` — `IInboxStore`, `DbConnectionInboxStore`
- `Runtime/Outbox/` — `OutboxBehavior`, `OutboxCapture`, `OutboxRelay`, `IOutboxFlusher`, `WrittenOutboxMessage`
- `Runtime/Inbox/` — `InboxInterceptor`, `InboxCleanup`

**Configuration:**
- `AddDurability()` extension on `ConduitBuilder` — configures database once, nests `AddOutbox()` and `AddInbox()` for feature-specific options. One `IDurabilityDialect` + one `IDurabilityConnectionFactory` shared by all features.
- `DurabilityBuilder` — fluent API: `UseSqlite(cs)`, `UseSqlServer<T>(cs)`, `UsePostgres<T>(cs)`, `UseMySql<T>(cs)`, `AddOutbox(configure?)`, `AddInbox(configure?)`.
- `IDurabilityDialect` — unified SQL dialect interface. Outbox SQL properties prefixed `Outbox*`, inbox properties prefixed `Inbox*`. One implementation per database engine. Row-locking strategies: `FOR UPDATE SKIP LOCKED` (PostgreSQL, MySQL), `ROWLOCK/UPDLOCK/READPAST` (SQL Server), none (SQLite, single-writer).

**Outbox** (transactional outbox for reliable F&F delivery):
- `IOutboundInterceptor` in core — generated mediator checks before transport sends for fire-and-forget paths only
- `OutboxCapture` — scoped `IOutboundInterceptor` + `IOutboxFlusher` that buffers `DispatchEnvelope`s. `FlushAsync` sends captured messages immediately from the in-memory buffer after transaction commit, claiming entries via `IOutboxStore.ClaimByMessageIdsAsync` (same row-locking as relay) and dispatching via `TransportDispatcher`. No database read — payloads come from memory. Marks sent on success, increments retry on failure. No-op when nothing captured. Safe to not call — relay picks up entries on its normal schedule.
- `IOutboxFlusher` — user-facing interface with `FlushAsync(CancellationToken)`. Always registered by `AddOutbox()`. Users inject this and call after transaction commit for immediate send. EF Core users can auto-wire via `SaveChangesInterceptor` (documented pattern, not built in).
- `OutboxBehavior<TRequest, TResult>` — pipeline behavior that writes captured messages to outbox store within handler's DB transaction. After writing, calls `OutboxCapture.SetWrittenMessages` to provide the MessageId-to-envelope correlation for `FlushAsync`.
- `OutboxRelay` — `BackgroundService` that polls for pending entries and sends via `TransportDispatcher`
- `IOutboxStore` interface with built-in `DbConnectionOutboxStore` (raw ADO.NET). Includes `ClaimByMessageIdsAsync` for immediate-send entry claiming.

**Inbox** (idempotent message processing):
- Application-level message deduplication for all inbound wire messages (commands, queries, events). Prevents duplicate handler execution when brokers redeliver messages. Duplicates are silently discarded — no result caching.
- `InboxInterceptor` — `IInboundInterceptor` gate that checks `MessageContext.Current.MessageId` against `IInboxStore`. If already recorded, returns false (discard). If new, records and returns true (proceed). No-op when no `MessageContext` (local dispatch).
- `IInboxStore` interface with `ExistsAsync`, `RecordAsync`, `DeleteExpiredAsync`, `EnsureTableExistsAsync`. Built-in `DbConnectionInboxStore` (raw ADO.NET). Table schema: `MessageId` (PK) + `ProcessedAt`. INSERT uses upsert/ignore semantics for concurrent safety.
- `InboxCleanup` — `BackgroundService` that periodically removes expired entries.
- `InboxOptions` — `RetentionPeriod` (default 30min), `CleanupInterval` (default 5min).
- **Edge case: record-then-crash** — if process crashes after `RecordAsync` but before handler completes, the message is recorded as processed but was never handled. At-most-once semantics. Standard trade-off (MassTransit/Wolverine same behavior). Users needing at-least-once should use broker-native retry without the inbox.
- **Broker-native dedup** — ASB `RequiresDuplicateDetection`, SQS FIFO `MessageDeduplicationId` are viable alternatives. Not abstracted by Conduit — configure via passthrough broker options.

### Conduit.Analysis (netstandard2.0)
- Target netstandard2.0 for Roslyn analyzer compatibility
- Use PolySharp NuGet for modern C# features (`required`, file-scoped namespaces, etc.)
- Contains shared type model only (`DispatchableTypeInfo`, `DispatchableKind`)

### Conduit.SourceGen (netstandard2.0)
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

### Conduit.Transport.Http
- `HttpTransport` — outbound C#→C# request/response via HTTP POST. `UseHttp(httpClient)` registers as keyed singleton by endpoint name and calls `UseTransport<HttpTransport>()`
- `MapConduitEndpoint()` — inbound HTTP wire dispatch endpoint, maps POST `/api/conduit/dispatch` and `/api/conduit/publish` (base path configurable)
- Wire format: request body = plain serialized message JSON, type in `X-Conduit-Type` header, `MessageContext` in `X-Conduit-Message-Id`/`X-Conduit-Causation-Id`/`X-Conduit-Conversation-Id` headers. Matches the broker wire format (type in transport-native properties, body is the raw message).
- `ConduitRestHandlers` — typed handler helpers for RESTful endpoint mapping (query→route binding, command→body binding)
- RESTful extension methods (`MapConduitGet<T>`, `MapConduitPost<T>`, etc.) are source-generated into `ConduitRestEndpoints` — conditionally emitted only when `Conduit.Transport.Http` is referenced
- Maps `DispatchResult` error codes to HTTP status codes via `ErrorCode.ToHttpStatusCode()` (wire dispatch endpoint); `ConduitException` → Problem Details (RESTful endpoints)

### Conduit.Transport.SignalR
- `ConduitHub` — inbound SignalR hub with `Dispatch(type, payload, context?)` and `Publish(type, payload, context?)` methods, injects `IWireMediator` via constructor. The `context` parameter is a `Dictionary<string, string>?` for `MessageContext` propagation — browser clients pass `null`, C# clients populate it.
- `SignalRTransport` — outbound dual-mode transport with lazy connect (`EnsureConnectedAsync`). `UseSignalR(hubUri)` registers as keyed singleton by endpoint name and calls `UseTransport<SignalRTransport>()`. Client mode sends `MessageContext.Current` as the context dictionary.
- `AddConduitSignalR()` registers SignalR services and server-mode transport as non-keyed `IConduitTransport` (for event broadcast to connected clients)

### Conduit.Transport.AzureServiceBus
- `AzureServiceBusTransport` — extends `BrokerTransportBase<ProcessMessageEventArgs>`. Inbound dispatch, lifecycle, and outbound serialization handled by base class. Implements ASB-specific primitives: session-based replies, topic publishing, processor management, topology creation.
- `UseAzureServiceBus(connectionString)` registers: singleton transport, `IHostedService` forwarding, non-keyed `IConduitTransport` (for event broadcast), keyed `IConduitTransport` (for endpoint routing), and calls `UseTransport<AzureServiceBusTransport>()`
- Wire format: message body = serialized message (JSON), type in `Subject` property, MessageContext in `MessageId`/`CorrelationId`/`CausationId` application property
- Request/response via ASB sessions on a reply queue (`{systemName}-{appName}-replies`)
- **All messages flow through the topic**: commands, queries, and events all publish to the shared topic. Subscriptions with SQL filters (`Subject = '{FullTypeName}'`) forward to the handler's app queue. Replies are the only exception (point-to-point to reply queue).
- **Unicast subscriptions** (commands/queries): shared name via `TopologyNaming.CreateSharedResourceName` — only one app receives each message
- **Multicast subscriptions** (events): per-app name via `TopologyNaming.CreateAppResourceName` — every subscribing app gets a copy
- Topology: try-create with 409 swallowing, always created on startup (idempotent). Supports topology modes (Production = durable, Developer = 4h auto-delete, Test = 5min auto-delete). Resource names generated by `TopologyNaming`.
- Receiver pools for concurrency isolation (`AddPool()` on options)
- Type discovery via `ConduitTypeRegistry` (source-generated, compile-time verified) — no runtime DI scanning

### Conduit.Transport.RabbitMQ
- `RabbitMQTransport` — extends `BrokerTransportBase<RabbitMQInboundMessage>`. Inbound dispatch, lifecycle, and outbound serialization handled by base class. Implements RMQ-specific primitives: AMQP channel management, topic exchange publishing, TCS-based reply correlation, consumer management.
- `UseRabbitMQ(connectionString)` registers: singleton transport, `IHostedService` forwarding, non-keyed `IConduitTransport` (for event broadcast), keyed `IConduitTransport` (for endpoint routing), and calls `UseTransport<RabbitMQTransport>()`
- Wire format: message body = serialized message (JSON), type in `Type` property, MessageContext in `MessageId` property + `CausationId`/`ConversationId` headers
- Request/response via exclusive auto-delete reply queue + `ReplyTo` + `CorrelationId` (used only for reply correlation, not MessageContext)
- **All messages flow through the topic exchange**: commands, queries, and events all publish to the single topic exchange with routing key = FullTypeName. Queue bindings route messages to the correct app queue. Reply messages bypass the exchange (direct to reply queue).
- Topology: idempotent `ExchangeDeclare`/`QueueDeclare`/`QueueBind`, always created on startup. Supports topology modes (Production = durable, Developer/Test = auto-delete). Resource names generated by `TopologyNaming`. No direct exchange — single topic exchange only. Pool-assigned types are excluded from the default app queue bindings to prevent dual delivery.
- Concurrency via `BasicQos` prefetch count
- Type discovery via `ConduitTypeRegistry` (source-generated, compile-time verified) — no runtime DI scanning

### Conduit.Transport.Kafka
- `KafkaTransport` — extends `BrokerTransportBase<KafkaInboundMessage>`. Uses `Confluent.Kafka`. Inbound dispatch, lifecycle, and outbound serialization handled by base class. Implements Kafka-specific primitives: producer/consumer management, consumer group routing, dead-letter topic publishing.
- `UseKafka(bootstrapServers, configure?)` registers: singleton transport, `IHostedService` forwarding, non-keyed `IConduitTransport` (for event broadcast), keyed `IConduitTransport` (for endpoint routing), and calls `UseTransport<KafkaTransport>()`
- Two consumer groups per app: shared for commands/queries (unicast), per-app for events (multicast). Both subscribe to the same topic; client-side filtering by message type.
- Reply topic per app with per-instance consumer group and manual partition assignment
- Wire format: `conduit-type`, `conduit-message-id`, `conduit-causation-id`, `conduit-conversation-id`, `conduit-reply-topic`, `conduit-correlation-id` headers (UTF-8 byte arrays). Message key = FullTypeName.
- `AbandonAsync` commits offset and publishes to dead-letter topic immediately (no in-process retry — Kafka offsets are sequential, not committing blocks all subsequent messages)
- Topology modes: Production = default retention, Developer = 4h retention, Test = 5min retention. Background cleanup task for ephemeral topics.
- Type discovery via `ConduitTypeRegistry` (source-generated, compile-time verified) — no runtime DI scanning

### Conduit.Transport.AmazonSqs
- `AmazonSqsTransport` — extends `BrokerTransportBase<AmazonSqsInboundMessage>`. Uses AWS SDK (`AmazonSNSClient` + `AmazonSQSClient`). Inbound dispatch, lifecycle, and outbound serialization handled by base class. Implements SNS/SQS-specific primitives: SNS publishing, SQS long-polling consumers, reply queue correlation, topology creation.
- `UseAmazonSqs(configure)` registers: singleton transport, `IHostedService` forwarding, non-keyed `IConduitTransport` (for event broadcast), keyed `IConduitTransport` (for endpoint routing), and calls `UseTransport<AmazonSqsTransport>()`. No connection string — uses Region or ServiceUri (for LocalStack).
- SNS topic for all messages, SQS queues per app, SNS subscriptions with filter policies route by type
- Commands/queries: shared subscription names (unicast). Events: per-app subscription names (multicast).
- Reply via direct SQS send (bypasses SNS). Message attributes: Type, MessageId, CausationId, ConversationId, ReplyQueueUrl, CorrelationId.
- Raw message delivery required on subscriptions (filter policies need it). Startup validates `RawMessageDelivery=true` and `VisibilityTimeoutSeconds >= ResponseTimeout`.
- SQS queue name limit is 80 characters — `TopologyNaming` hash truncation activates frequently.
- Topology modes: Production = standard, Developer/Test = tagged resources with background cleanup.
- Type discovery via `ConduitTypeRegistry` (source-generated, compile-time verified) — no runtime DI scanning

### Conduit.Transport.ActiveMQ
- `ActiveMQTransport` — extends `BrokerTransportBase<ActiveMQInboundMessage>`. Uses `AmqpNetLite` (AMQP 1.0). Inbound dispatch, lifecycle, and outbound serialization handled by base class. Implements Artemis-specific primitives: AMQP 1.0 connection/session management, two-address publishing, exclusive reply queue correlation, receiver link management.
- `UseActiveMQ(connectionString, configure?)` registers: singleton transport, `IHostedService` forwarding, non-keyed `IConduitTransport` (for event broadcast), keyed `IConduitTransport` (for endpoint routing), and calls `UseTransport<ActiveMQTransport>()`
- Two addresses: anycast for commands/queries, multicast for events
- Command/query queues: shared names on anycast address (unicast). Event queues: per-app names on multicast address (multicast).
- Reply queue: per-instance exclusive temporary queue (destroyed on shutdown)
- AMQP 1.0 wire mapping: Subject=FullTypeName, MessageId, CorrelationId=ConversationId, ApplicationProperties for CausationId/ReplyTo/ReplyCorrelationId
- Topology via Artemis management address (`activemq.management`). Topology modes: Production = durable, Developer = auto-delete-delay 4h, Test = auto-delete-delay 5min (native Artemis support).
- Concurrency via AMQP link credit (prefetch count)
- Type discovery via `ConduitTypeRegistry` (source-generated, compile-time verified) — no runtime DI scanning

### Conduit.TypeGen
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

### Conduit.Exceptions
- `ErrorCode` — enumeration class pairing a machine-readable code string with HTTP status code. Value-based equality. Built-in codes: `NotFound`, `Unauthorized`, `Conflict`, `InternalError`, `BadRequest`. `ErrorCode.ToHttpStatusCode(string)` maps wire code strings to HTTP status codes (single source of truth for both RESTful and envelope endpoints). Consumers define their own.
- `ConduitException` — abstract base with `ErrorCode Code` property
- Concrete: `NotFoundException`, `UnauthorizedException`, `ConflictException` — all sealed
- `EventListenerException` — infrastructure exception (not a `ConduitException`) thrown when event listeners fail during wire dispatch. Carries `IReadOnlyList<Exception> ListenerExceptions`. Transports catch this to decide complete vs abandon.

### Sample Apps
- `samples/browser-client/Conduit.Sample.Notes/` — browser-facing single service with SignalR hub, notes CRUD, events pushed to browser. Vite + TypeScript frontend in `ClientApp/` consuming generated `ConduitClient` + `SignalRTransport`
- `samples/service-to-service/Conduit.Sample.Orders/` — order service that routes commands to InventoryService via SignalR, exposes HTTP dispatch
- `samples/service-to-service/Conduit.Sample.Inventory/` — internal inventory service handling commands via SignalR hub
- `samples/rest-api/Conduit.Sample.Todos/` — REST API demonstrating `MapConduitGet/Post/Put/Delete` with OpenAPI/Swagger UI

## Design Spec

Full design specification at `docs/design.md` — covers interfaces, generated code patterns, TypeScript output structure, validation strategy, auth model, and future Rebus integration plans.
