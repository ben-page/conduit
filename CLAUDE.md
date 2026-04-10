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
- **ErrorCode enumeration class**: `ConduitException.Code` is an `ErrorCode` record that co-locates the machine-readable code string with its HTTP status code. Equality is based on `Value` only. RESTful endpoints use `Code.HttpStatusCode` directly for Problem Details responses. The envelope endpoint uses `ErrorCode.ToHttpStatusCode()` to map the string-based `DispatchError.Code` from the wire format.
- **DispatchResult invariants**: `DispatchResult<T>` uses factory methods `Ok(data)` and `Fail(code, message, details?)` with a `[JsonConstructor]` private constructor for deserialization.
- **TypeScript generation**: Types marked `[ClientVisible]` get TypeScript interfaces emitted by `Conduit.TypeGen`. This includes both dispatchable types and non-dispatchable DTOs/models. TypeGen emits a self-contained runtime (`conduit.ts`) with transports and a per-project file (`conduit.generated.ts`) with `ConduitClient` — no npm package needed. `ConduitClient` is a thin dispatch client (not a mediator) with `execute`/`fetch`/`publish`/`on` methods.
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

## Project-Specific Notes

Each project has its own `CLAUDE.md` with implementation-level detail. These are auto-loaded when Claude touches files in that directory — read them directly when working on that subsystem:

- [`src/Conduit/CLAUDE.md`](src/Conduit/CLAUDE.md) — core runtime types (`ConduitRuntime`, `TransportDispatcher`, `BrokerTransportBase`, `ConduitTypeRegistry`), configuration, exceptions
- [`src/Conduit.Analysis/CLAUDE.md`](src/Conduit.Analysis/CLAUDE.md) — shared type model (netstandard2.0)
- [`src/Conduit.SourceGen/CLAUDE.md`](src/Conduit.SourceGen/CLAUDE.md) — Roslyn source generator internals, emitted files, attribute handling
- [`src/Conduit.TypeGen/CLAUDE.md`](src/Conduit.TypeGen/CLAUDE.md) — MSBuild task, MetadataLoadContext scanning, TypeScript emission
- [`src/Conduit.Durability/CLAUDE.md`](src/Conduit.Durability/CLAUDE.md) — outbox + inbox implementation, dialects, configuration
- [`src/Conduit.Transport.Http/CLAUDE.md`](src/Conduit.Transport.Http/CLAUDE.md)
- [`src/Conduit.Transport.SignalR/CLAUDE.md`](src/Conduit.Transport.SignalR/CLAUDE.md) — dual-mode server/client transport
- [`src/Conduit.Transport.AzureServiceBus/CLAUDE.md`](src/Conduit.Transport.AzureServiceBus/CLAUDE.md) — includes emulator test caveats
- [`src/Conduit.Transport.RabbitMQ/CLAUDE.md`](src/Conduit.Transport.RabbitMQ/CLAUDE.md)
- [`src/Conduit.Transport.Kafka/CLAUDE.md`](src/Conduit.Transport.Kafka/CLAUDE.md)
- [`src/Conduit.Transport.AmazonSqs/CLAUDE.md`](src/Conduit.Transport.AmazonSqs/CLAUDE.md)
- [`src/Conduit.Transport.ActiveMQ/CLAUDE.md`](src/Conduit.Transport.ActiveMQ/CLAUDE.md)

## Sample Apps

- `samples/browser-client/Conduit.Sample.Notes/` — browser-facing single service with SignalR hub, notes CRUD, events pushed to browser. Vite + TypeScript frontend in `ClientApp/` consuming generated `ConduitClient` + `SignalRTransport`
- `samples/service-to-service/Conduit.Sample.Orders/` — order service that routes commands to InventoryService via SignalR, exposes HTTP dispatch
- `samples/service-to-service/Conduit.Sample.Inventory/` — internal inventory service handling commands via SignalR hub
- `samples/rest-api/Conduit.Sample.Todos/` — REST API demonstrating `MapConduitGet/Post/Put/Delete` with OpenAPI/Swagger UI

## Design Spec

Full design specification at `docs/design.md` — covers interfaces, generated code patterns, TypeScript output structure, validation strategy, auth model, and future Rebus integration plans.
