# Conduit (core)

No ASP.NET dependency — only DI abstractions, Hosting abstractions, Logging abstractions, Options,
and `Microsoft.Extensions.Configuration.Binder`.

## Runtime types

- `ConduitRuntime` — static class centralizing route resolution, transport resolution (keyed DI), inbound gate chain, pipeline chaining, error handling. `HandleWireRequest`/`HandleWireEvent` run `IInboundInterceptor` gates before deserialization, then dispatch. Called primarily by source-generated mediator code.
- `TransportDispatcher` — singleton service encapsulating route resolution + transport dispatch. `SendAsync(envelope, ct)` resolves routes via `ConduitRuntime.ResolveRoute`, then sends via the endpoint's keyed transport (`SendRequestAsync`) or broadcasts to all transports (`SendEventAsync`). Registered by `AddConduitCore`. Used by `OutboxRelay` and `OutboxCapture.ClaimAndDeliverAsync` (the routine `OutboxCapture.FlushAsync` schedules on `ConduitBackgroundDispatcher`) instead of duplicating transport resolution logic.
- `TypeFilterInterceptor` (in `Conduit.Middleware`) — `IInboundInterceptor` gate that throws `ForbiddenException` (403, not `UnauthorizedException`/401 — the caller is authenticated, the type just isn't client-dispatchable) for types not in `TypeFilterOptions.AllowedTypes`. Configured via `conduit.AddTypeFilter()`.
- `TopologyNaming` — generates shared resource names (`CreateSharedResourceName`) and app-scoped resource names (`CreateAppResourceName`) with consistent prefix/truncation/hashing across all transports.
- `ConduitTypeRegistry` — compile-time registry of event, command, and query message types. Properties: `EventTypes`, `CommandTypes`, `QueryTypes` (Type lists) and pre-computed `EventTypeNames`, `CommandTypeNames`, `QueryTypeNames` (string lists). Populated by source generator in `AddConduit()` with `typeof()` expressions. Broker transports resolve from DI for topology discovery instead of scanning `IServiceCollection` at runtime.
- `ApplicationName` on `ConduitOptions` defaults from `IHostEnvironment.ApplicationName` at first resolution via factory registration in `AddConduitCore`. No transport-side mutation needed.
- `ConduitBackgroundDispatcher` — `BackgroundService` with unbounded `Channel<T>` for F&F work items. Registered in DI by `AddConduitCore`. Sequential execution, linked cancellation for graceful shutdown.
- `BrokerTransportBase<TMessage>` — abstract base class for all 5 broker transports (ASB, RMQ, Kafka, SQS, ActiveMQ). Owns `IHostedService` lifecycle, inbound dispatch orchestration (context save/restore, the self-published-event filter, scope creation, request/event branching, error handling), and outbound send (serialization, reply unwrap via `ConduitRuntime.UnwrapReply`). Transports implement ~15 abstract methods for transport-specific primitives. Uses `BinaryData` for payload passing. `StartAsync` branches on `ProducerOnly`: true → `ConnectAsync` + `EnsureOutboundTopologyAsync`; false → `ConnectAsync` + `EnsureTopologyAsync` + `StartConsumersAsync`. `EnsureOutboundTopologyAsync` is an abstract method for producer-only topology (reply queue + topic/exchange only). Outbound metadata is assembled once per publish by the base class into a fresh `Dictionary<string, string>` keyed by `ConduitHeaders.*` — correlation IDs from `MessageContext.Current`, W3C trace context from the current `Activity`, and the publishing application's name (`ConduitOptions.ApplicationName`, omitted when unset) — and passed to `PublishRequestAsync`/`PublishEventAsync`. Transports are dictionary-to-native-property mappers and keep their historical on-wire names; no transport holds ambient outbound state.
- `ConduitHeaders` — well-known keys for the outbound header dictionary `BrokerTransportBase` assembles once per publish (`MessageId`, `CausationId`, `ConversationId`, `SourceApp`). These name **in-process** dictionary entries; each transport maps them onto its own native property model and keeps its existing on-wire names. Transports must not read ambient state (`MessageContext.Current`, static fields) on the outbound path — the dictionary is the only source.
- **Self-published event filter** (`BrokerTransportBase.ProcessInboundMessageAsync`) — drops an inbound event whose `ConduitHeaders.SourceApp` stamp matches this host's own `ApplicationName`, because that application's listeners already ran inline when `PublishAsync` published it. An application's own listeners therefore always run **on the publishing instance**, never load-balanced across replicas — the fix removes the extra broker-delivered execution rather than redistributing the work. Placed immediately after `MessageContext.StartDispatchFromTransport` (so the skip observation isn't dropped — `MessageContext.Observe` no-ops without a current context) and before `CreateAsyncScope` (so a filtered message costs neither a DI scope nor a deserialize); acknowledges rather than abandons, to avoid a redelivery loop. Four details make the guard correct: it keys on the **application**, not the instance — `ApplicationName` is shared by every replica, so an instance-scoped stamp would let a peer replay the listeners; it recognizes events via `ConduitTypeRegistry.EventTypeNames`, not `!IsRequest` — a routed `[FireAndForget]` command also arrives with `IsRequest == false`, and filtering on that alone would silently drop a command an application routed to itself or a peer; it requires `ApplicationName` to be non-empty before comparing — without that guard, a missing stamp on *any* inbound event, including one from a non-Conduit producer, would compare equal to an unset `ApplicationName` and be silently discarded, so an unnamed application would drop every inbound event rather than none; and a missing or non-matching stamp always dispatches normally, so pre-upgrade messages and non-Conduit producers are unaffected. Emits `SelfPublishedEventSkipped` (a `MessageObservation`) on drop, so a dropped listener run is visible to any registered `IConduitObserver` (see [Audit](../../docs/audit.md)) instead of silently disappearing.
- `BrokerTransportOptions` — abstract base for all 5 broker options classes. Owns `DefaultConcurrency` (int, default 10), the `Pools` list, and `AddPool(name, configure)`. Concrete transport options inherit it and add only broker-specific settings.
- `ReceiverPoolOptions` — single shared named-pool config: `Name` (required), `Concurrency` (int, default 1), `MessageTypes` (`IReadOnlyList<string>`, exposed read-only; populate via `Handle(fullTypeName)`, `Handle<TMessage>()`, or `Handle(Type)` — all three resolve to `FullTypeName` and de-duplicate, so calling any of them twice with the same type is a no-op). `Concurrency` is interpreted per broker (prefetch for RabbitMQ/ActiveMQ, max-concurrent-calls for ASB, polling parallelism for SQS, consume parallelism for Kafka); transports cast to the broker-native type at the consumption site (e.g. `(ushort)` for RabbitMQ prefetch).
- `ConduitConfigurationBinder` (`Conduit.Configuration`) — static helper, `BindTransportOptions(BrokerTransportOptions options, IConfiguration? section, params string[] excludedKeys)`. Called by each broker transport's `Use*` extension before its `configure` callback runs. No-ops when `section` is null or has no children. Validates every `Pools` entry has a unique, non-empty `Name` (the binder itself would bind a missing required `Name` as `""` rather than throwing) — throws `InvalidOperationException` naming the offending configuration path otherwise. Binds everything except `excludedKeys` (and null values) by projecting the section through `AsEnumerable(makePathsRelative: true)`, rebuilding it as an in-memory configuration, and calling `Bind(options)` — `excludedKeys` exists because the binder throws on abstract/interface-typed properties it cannot construct (e.g. `AmazonSqsOptions.Credentials`). Then hand-adds each pool's `MessageTypes` via `Handle(...)`, because `ReceiverPoolOptions.MessageTypes` is a getter-only `IReadOnlyList<string>` that the binder skips silently.

## Configuration

`AddConduit()` is generated by source gen. All configuration goes through `ConduitBuilder`: `AddEndpoint`, `AddPipeline`, `AddTypeFilter`. Routing is configured per-endpoint via `EndpointBuilder.RouteNamespace<T>()` and `EndpointBuilder.RouteType<T>()`.

`TopologyMode` enum (Production/Developer/Test) and `SystemName`/`TopologyPrefix` properties on `ConduitOptions` control topology resource naming and durability across broker transports.

`ConduitBuilder.UseConfiguration(IConfiguration)` binds the five `ConduitOptions` scalars
(`SystemName`, `ApplicationName`, `TopologyMode`, `TopologyPrefix`, `ProducerOnly`) explicitly,
property by property — never `section.Bind(options)`, because `Endpoints`/`Routes`/`PipelineTypes`
are code-owned shapes the binder would mangle or choke on. It stores the resolved section on
`ConduitBuilder.Configuration` for `EndpointBuilder` to slice into `Endpoints:{name}` per endpoint.
Source-generated `AddConduit(IServiceCollection, IConfiguration, Action<ConduitBuilder>?)` (emitted
by `RegistrationEmitter`, `src/Conduit.SourceGen/`) just calls `UseConfiguration` then the callback —
config binds first, code always wins.

`BrokerTransportOptions.AddPool(name, configure)` is **get-or-create**: if a pool with that name
already exists (typically because configuration produced it), `configure` runs against the existing
pool instead of appending a duplicate. Name matching is `OrdinalIgnoreCase`; since configuration
binds first and creates the instance, the configuration's spelling of `Name` is what survives into
broker resource naming. Combined with `ReceiverPoolOptions.Handle`'s de-duplication,
a pool declared in both configuration and code ends up with the union of both sources' message
types rather than two competing pools. See [`docs/configuration-reference.md`](../../docs/configuration-reference.md#loading-from-iconfiguration)
for the full picture, including per-transport configuration key tables.

**Measured binder behaviours.** Every row below was verified against the real
`Microsoft.Extensions.Configuration.Binder` (with the real `Confluent.Kafka` and `AWSSDK.SQS`
types), not inferred from its documentation. They are the reason `ConduitConfigurationBinder` has
the shape it has — re-measure before "simplifying" any step away, because each one that goes back
takes a silent misconfiguration with it.

| Binder behaviour | Consequence in the code |
|---|---|
| A getter-only `List<ReceiverPoolOptions> Pools` **does** bind — entries are created with their `Name` and `Concurrency` | pools never need hand-constructing; the helper only fixes up what the binder misses |
| A getter-only `IReadOnlyList<string> MessageTypes` is **silently skipped** — the pool binds but routes nothing | `BindTransportOptions` re-walks the pool sections afterwards and hand-adds each type via `Handle(...)` |
| A `Pools` entry missing the `required` `Name` binds as `""` rather than throwing | `ValidatePoolNames` runs first and throws `InvalidOperationException` naming the offending configuration path |
| A blanket `Bind` **throws** on an abstract/interface-typed property (`AmazonSqsOptions.Credentials` is an `AWSCredentials`) and binds `RegionEndpoint` to **null silently** | `excludedKeys` projects a key and its whole subtree out before binding; Amazon SQS excludes `Region` + `Credentials` and hand-maps both |
| Dotted librdkafka keys (`ProducerConfig:linger.ms`) bind **nothing** — the binder matches C# property names only | `UseKafka`'s `ApplyDottedKeys` second pass feeds them through `ClientConfig.Set`, so both spellings work |
| `ClientConfig` implements `IEnumerable<KeyValuePair<string, string>>`, but the binder treats it as an **object** and writes through the property setters into the underlying librdkafka dictionary | `ProducerConfig`/`ConsumerConfig` bind natively — no hand-mapping needed |
| Non-public setters are skipped | `EndpointName { get; internal set; }` cannot be hijacked from configuration — the endpoint's identity is not a setting |
| Enum members bind by name (`"Developer"`), `TimeSpan` parses from `"00:00:30"`, and an absent key yields null | `UseConfiguration` assigns only when the key is present, so unset keys leave defaults alone |

**Rejected alternatives** (recorded so they are not re-proposed without new information):

- `services.AddOptions<T>().BindConfiguration(...)` with `IOptions<T>` resolution — idiomatic, and it would enable reload-on-change, but it defers binding past registration, which is exactly where the required-value validation runs, and it changes how all seven transports consume their options.
- A `ConnectionStrings:{endpointName}` fallback — a familiar .NET convention, but it puts the same value in two discoverable places and mirrors no builder call, breaking the key-name rule.
- Config wins over code — rejected in favour of code-wins so an explicit `UseRabbitMQ("amqp://prod")` can never be silently redirected by a stray appsettings entry.
- `Conduit:Endpoints:{name}:{Transport}` nesting — redundant, since an endpoint has exactly one transport. And `Conduit:Transports:{Transport}` — cannot express two endpoints on the same broker technology with different connection strings.
- Growing `ConduitEndpoint` to hold its transport options so the runtime object graph matches the configuration shape — core cannot reference the transport packages, so the property would have to be `object?`, a weaker contract than the keyed DI registration that already owns it.

**Trim/AOT.** `Microsoft.Extensions.Configuration.Binder` is reflection-based, so the
`AddConduit(IConfiguration, ...)` path is the one reflective seam in an otherwise source-generated
library. It runs at registration time only, and only for applications that opt into it. Nothing in
the repo sets `IsTrimmable` today, so this is a constraint to settle before publishing trimmable or
AOT-friendly packages rather than a present defect — but it does qualify the "reflection-free,
compile-time" positioning, and a code-only host never touches it.

## Wire dispatch route forwarding

The generated `DispatchRequestAsync` checks routes before the local type switch. Unknown types matching a route are forwarded as raw envelopes to the transport without deserializing locally.

## Exceptions

- `ErrorCode` — enumeration class pairing a machine-readable code string with HTTP status code. Value-based equality. Built-in codes: `NotFound`, `Unauthorized`, `Forbidden`, `Conflict`, `PreconditionFailed`, `InternalError`, `BadRequest`, `ContractMismatch`. `ErrorCode.ToHttpStatusCode(string)` maps wire code strings to HTTP status codes (single source of truth for both RESTful and envelope endpoints). Consumers define their own.
- `ConduitException` — abstract base with `ErrorCode Code` property. `GetClientData()`/`GetClientType()` expose a subclass's own public properties (declared below `ConduitException`, via `ClientDataProperties(Type)`) to the wire; `null` for a plain status exception. `DispatchResult<T>.Fail(ConduitException)` carries that onto `DispatchError.Status`/`Type`/`Data`.
- `ContractMismatchException` (sealed, code `ContractMismatch`, 400) -- thrown by `ConduitRuntime.DeserializeWire` when a wire request omits a constructor parameter that has no C# default, at any depth of positional models; `Details` = one `(wirePath, "missing")` per member (`eTag`, `columns[0].wipLimit`), computed from the contract walked against the payload. `DeserializeWire` reads with a private `WireReadOptions` (`JsonOptions` + `RespectRequiredConstructorParameters`); NEVER set that flag on the shared `JsonOptions` -- it also reads stored job payloads, replies and events, which must stay lenient. A settable/`init` property of a non-positional model is not checked; the RESTful endpoints read leniently; broker-delivered and DLQ-replayed commands are strict (a broker-routed command's new member needs a default). Naming failures fall back to `BadRequest`, never a 500.
- Concrete: `BadRequestException`, `NotFoundException`, `UnauthorizedException`, `ForbiddenException`, `ConflictException`, `PreconditionFailedException` — not sealed; subclass to declare a refusal's client-facing data as public properties
- `EventListenerException` — infrastructure exception (not a `ConduitException`) thrown when event listeners fail during wire dispatch. Carries `IReadOnlyList<Exception> ListenerExceptions`. Transports catch this to decide complete vs abandon.
