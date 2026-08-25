# Glossary

Concise definitions of the domain vocabulary used across Conduit. Where a concept has a
dedicated guide, a link points to deeper material.

### Endpoint

A **named** destination registered via `ConduitBuilder.AddEndpoint("name", ...)`. An endpoint is
just a name plus a transport: the transport is stored in DI as a **keyed** `IConduitTransport`
singleton (key = endpoint name) and resolved at dispatch time by `ConduitRuntime.ResolveTransport`.
See [Transports](transports/index.md).

### Route

A mapping that decides **where** a command or query type goes. Configured per-endpoint via
`endpoint.RouteNamespace<T>()` (all types sharing `T`'s namespace) or `endpoint.RouteType<T>()`
(one type). `ConduitRuntime.ResolveRoute` matches a message's full type name by namespace/type
prefix; no match means **local dispatch**. Events do not route — they broadcast.

### Topology

The set of broker resources (topics/exchanges, queues, subscriptions/bindings) and their naming
and lifetime, generated consistently across transports by the `TopologyNaming` static class.

- **TopologyMode** — enum on `ConduitOptions` (`Production` / `Developer` / `Test`) controlling
  resource naming and durability. Developer/Test use ephemeral resources (Developer = 4h
  auto-delete, Test = 5min auto-delete); Production uses durable resources.
- **TopologyPrefix** — a single settable `ConduitOptions` property that auto-resolves on first
  access: Developer defaults to `Environment.UserName`, Test generates a random 13-char prefix,
  Production resolves to `null`. Set it explicitly to share a prefix across hosts in integration tests.
- **SystemName** — default `"conduit"`; the base name for topology resources. Replaces per-transport
  topic/exchange name configuration.

### Gate (`IInboundInterceptor`)

The **pre-deserialization** middleware layer for wire-inbound messages. Receives only the
`string messageType` and returns `bool` to allow (`true`) or reject (`false`) processing before the
payload is deserialized. Used by `InboxInterceptor` (dedup — returns `false` to discard) and
`TypeFilterInterceptor` (access control — **throws** to reject). `MessageContext.Current` is
available so a gate can read the transport-supplied `MessageId`. See
[dispatch flows](dispatch-flows.md#3-wire-inbound-message).

### Interceptor (inbound / outbound / error)

- **Inbound** — `IInboundInterceptor`, the gate above.
- **Outbound** — `IOutboundInterceptor`. When registered, the generated mediator calls
  `Intercept(envelope)` *instead of* sending an outbound fire-and-forget message to the transport.
  Used by the outbox to capture sends. See [Outbox](outbox.md).
- **Error** — `IInboundErrorInterceptor`, invoked when inbound dispatch fails (e.g. dead-letter capture).

### Pipeline behavior (`IPipelineBehavior<TRequest, TResult>`)

The **post-deserialization** middleware layer that wraps the strongly-typed handler with a
`next()` chain, uniformly for commands, queries, and events. Registered as open generics via
`conduit.AddPipeline(typeof(MyPipeline<,>))`. Ordering is owned by Conduit (stored in
`ConduitOptions.PipelineTypes`, resolved by `ConduitRuntime.ResolvePipelines`); **first registered =
outermost**. See [Pipeline Behaviors](pipeline-behaviors.md).

### Envelope (`DispatchEnvelope`)

The internal handoff type carrying the type discriminator (`Type`) and serialized `Payload`
(`JsonElement`) between the mediator and a transport. It is **not** exposed on the wire — the wire
body is the plain serialized message, with the discriminator in transport-native properties.

### Transport (`IConduitTransport`)

A bidirectional message mover with `SendRequestAsync` (request/response) and `SendEventAsync`
(fire-and-forget events), both envelope-based. Each transport package contains **both sides**:

- **Outbound** — the client that sends (e.g. `HttpTransport`, `SignalRTransport`, the broker
  transport's publish path).
- **Inbound** — the endpoint that receives (e.g. `MapConduitEndpoint()`, `ConduitHub`, the broker
  transport's consumer / `IHostedService`).

Handlers never reference a transport. See [Transports](transports/index.md).

### Observation / observer

An **observation** (`MessageObservation`) is a record emitted during dispatch via
`MessageContext.Observe(...)`. An **observer** (`IConduitObserver`) receives observations plus the
current `MessageContext`. The audit subsystem registers an observer to build the audit trail; types
marked `[DoNotAudit]` suppress payload capture. See [Audit](audit.md).

### ConduitBackgroundDispatcher (fire-and-forget)

A `BackgroundService` with an unbounded `Channel<T>` that executes fire-and-forget work items
sequentially. Local `[FireAndForget]` void commands and `[FireAndForget]` event listeners are
enqueued here via `Enqueue(...)`; pipeline behaviors are resolved in the caller context before
enqueuing. See [dispatch flows](dispatch-flows.md#fire-and-forget-void-commands).

### ProducerOnly

A `ConduitOptions` flag making a broker transport host **publish-only**: it skips inbound topology
(subscriptions, bindings) and consumers, but still creates reply queues for request/response. Use
for API hosts that route commands to workers, or test sender hosts.

### Wire format / type discriminator (`FullTypeName`)

The wire body is the **plain serialized message** (camelCase JSON). The message type is carried as a
**type discriminator** — the full type name (e.g. `"Orders.Commands.CreateOrder"`) — in
transport-native properties: HTTP `X-Conduit-Type` header, a SignalR hub method parameter, AMQP
`Type`/`Subject`, a Kafka header, etc. The `DispatchEnvelope` wrapper is never serialized onto the wire.

### MessageContext (`MessageId` / `CausationId` / `ConversationId`)

`AsyncLocal`-backed correlation context flowing through a dispatch chain.

- **MessageId** — unique ID for this specific dispatch.
- **CausationId** — the `MessageId` of the message that caused this one. A head/origin message
  self-causates, so this equals its own `MessageId` for the first message in a chain. Never null
  (non-nullable `string`).
- **ConversationId** — shared across the whole causal chain (the first message's `MessageId`).

`StartDispatch()` mints a fresh context for local dispatch (inheriting `ConversationId`, setting
`CausationId` to the parent's `MessageId`; a head dispatch self-causates so `CausationId == MessageId`).
`StartDispatchFromTransport(messageId, causationId, conversationId)` rebuilds the context from inbound
wire values so correlation propagates across services — if the wire omits `causationId`, it defaults to
`messageId` (self-causation). See [dispatch flows](dispatch-flows.md).

### Self-published event filter

A check in `BrokerTransportBase.ProcessInboundMessageAsync` that drops an inbound event whose
`conduit-source-app` header (`ConduitHeaders.SourceApp`) matches this host's own `ApplicationName`.
Exists because a broker's per-app event queue is bound to every discovered event type, including
ones the application publishes itself — without the filter, an application would run its own event
listeners twice: once inline at publish time, once again when the broker hands the message back.
Applies to events only, keyed on the application (not the instance) so any replica drops the
application's own copy. A dropped event is recorded as a `SelfPublishedEventSkipped` observation.
See [Transports → Self-published events](transports/index.md#self-published-events).

### Reply queue

A queue (broker transports) the sender owns for receiving request/response replies. Replies
**bypass** the topic/exchange and are delivered point-to-point to the reply queue. Reply queues are
created by `EnsureOutboundTopologyAsync` even in `ProducerOnly` mode.

---

See also: [Core Concepts](core-concepts.md) · [Dispatch Flows](dispatch-flows.md) ·
[Diagnostics](diagnostics.md) · [Configuration Reference](configuration-reference.md).
