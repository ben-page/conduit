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
`endpoint.RouteNamespace<T>()` (all types in `T`'s namespace and the namespaces beneath it) or
`endpoint.RouteType<T>()` (exactly one type). `ConduitRuntime.ResolveRoute` matches a message's full
type name exactly for a type route, and on whole namespace segments for a namespace route
(`Orders.Commands` covers `Orders.Commands.Lines.AddLine`, never `Orders.CommandsArchive.*`); the first
matching route wins, and no match means **local dispatch**. Events do not route — they broadcast.

### Topology

The set of broker resources (topics/exchanges, queues, subscriptions/bindings) and their naming
and lifetime, generated consistently across transports by the `TopologyNaming` static class.

- **TopologyMode** — enum on `ConduitOptions` (`Production` / `Developer` / `Test`) controlling
  resource naming and lifetime: durable in Production, ephemeral where the broker can delete a resource
  itself in Developer and Test. See [Developer and Test modes](transports/index.md#developer-and-test-modes).
- **TopologyPrefix** — the `ConduitOptions` prefix of every resource name, resolved on first read when
  unset: `Environment.UserName` in Developer mode, `test-` and 8 random hex characters in Test mode,
  `null` in Production. Set it explicitly to share a prefix across hosts in integration tests.
- **SystemName** — default `"conduit"`; the base name for topology resources. Replaces per-transport
  topic/exchange name configuration.

### Gate (`IInboundInterceptor`)

The **pre-deserialization** middleware layer for wire-inbound messages, requests and events alike.
Receives only the `string messageType` and returns `bool`: `true` lets the message through, `false`
discards it silently (a request is answered with an empty success). A gate that throws a
`ConduitException` refuses the message, and the refusal is answered to the caller. Used by
`InboxInterceptor` (dedup — returns `false` to discard a duplicate) and the type filter (access
control — throws `ForbiddenException` to refuse). `MessageContext.Current` is set by every transport
before the gates run, so a gate can read the transport-supplied `MessageId`. See
[dispatch internals](dispatch-flows.md#3-wire-inbound-message).

### Interceptor (inbound / outbound / error)

- **Inbound** — `IInboundInterceptor`, the gate above.
- **Outbound** — `IOutboundInterceptor`. When registered, the generated mediator offers it each
  message it could defer (an event, a routed `[FireAndForget]` command) with `Intercept(envelope)`
  before sending it. `true` means the interceptor captured it and the mediator skips the send;
  `false` means it declined, and the send proceeds as if no interceptor were registered. Used by the
  outbox, which captures only inside a database transaction. See [Outbox](outbox.md).
- **Error** — `IInboundErrorInterceptor`, invoked when a broker transport's inbound dispatch fails: every registered one runs (e.g. dead-letter capture, and the inbox releasing the failed message's record).

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

A bidirectional message mover with three envelope-based methods: `SendRequestAsync`
(request/response), `SendEventAsync` (no reply: an event's broadcast, a routed `[FireAndForget]`
command) and `RedeliverAsync` (a dead-letter replay, sent straight to the service's inbound queue).
Each transport package contains **both sides**:

- **Outbound** — the client that sends (e.g. `HttpTransport`, `SignalRTransport`, the broker
  transport's publish path).
- **Inbound** — the endpoint that receives (e.g. `MapConduitEndpoint()`, `ConduitHub`, the broker
  transport's consumer / `IHostedService`).

Handlers never reference a transport. See [Transports](transports/index.md).

### Observation / observer

An **observation** (`MessageObservation`) is a record emitted during dispatch via
`MessageContext.Observe(...)`. An **observer** (`IConduitObserver`) receives observations plus the
current `MessageContext`. The audit subsystem registers an observer to build the audit trail; types
marked `[DoNotAudit]` suppress payload capture, and a property or positional-record parameter marked
`[DoNotAudit]` is dropped from the captured body (`ConduitRuntime.AuditJsonOptions`; wire
serialization is unaffected); a member marked `[AuditRedacted]` is captured as the placeholder `"•••"` when it has a
value, never the value itself; a raw-JSON member marked `[AuditAs]` is captured as the message it holds, redacted
by that message's own marks. See [Audit](audit.md).

### IWireExceptionTranslator

A `Conduit.Runtime` extension point, `ConduitException? Translate(Exception)`, that lets a host map an
exception thrown by a handler to a refusal. `HandleWireRequest` consults every registered translator
in its catch-all before answering `InternalError`; a claimed exception becomes that refusal, with no
support reference. A translator that throws is logged and skipped. Typical use: mapping a data
layer's concurrency exception to `PreconditionFailedException` (412). See
[Error handling → Translating an unexpected exception](error-handling.md#translating-an-unexpected-exception).

### Refusal text (`RefusalText`)

The name of a refusal's sentence in the host's catalog, plus the values that fill it, carried beside the English
message by `ConduitException.Text` and `DispatchErrorDetail.Text`. A value that is itself a word is a `TextName`,
translated before it fills the sentence. Never on the wire: Conduit writes the sentence in the reader's language
where an answer leaves the server for a person (the envelope endpoint, the hub, REST), through the host's
`IRefusalTextRenderer`, and keeps the English everywhere else. See [Error handling → A refusal in the reader's language](error-handling.md#a-refusal-in-the-readers-language).

### Culture resolver (`IDispatchCultureResolver`)

A host service that answers the reader's culture for a dispatch from its `MessageContext`, or null for English.
Conduit asks it only where an answer leaves the server for a person and never sets the thread's culture from it.
`DispatchPropertyCultureResolver` is the ready-made one: one dispatch property mapped to cultures, anything unmapped
answering null. See [Error handling → A refusal in the reader's language](error-handling.md#a-refusal-in-the-readers-language).

### ConduitBackgroundDispatcher (fire-and-forget)

A `BackgroundService` with an unbounded `Channel<T>` that executes fire-and-forget work items
sequentially. Local `[FireAndForget]` void commands and `[FireAndForget]` event listeners are
enqueued here via `Enqueue(...)`; pipeline behaviors are resolved in the caller context before
enqueuing. See [dispatch internals](dispatch-flows.md#fire-and-forget-void-commands).

### ProducerOnly

A `ConduitOptions` flag making a broker transport host **publish-only**: it skips inbound topology
(subscriptions, bindings) and consumers, but still creates reply queues for request/response. Use
for API hosts that route commands to workers, or test sender hosts.

### Closed set

A class with a private constructor whose instances are a fixed list (`IClosedSet<TSelf>`, `Conduit.Messages`), each
spelled on the wire as its `Value` (`ConduitRuntime.JsonOptions`). `[ClosedSet(...)]` (`Conduit.Attributes`) states the
values again for TypeGen, which emits a string-literal union of them. See
[TypeScript client](transports/typescript-client.md#closed-sets).

### Wire format / type discriminator (`FullTypeName`)

The wire body is the **plain serialized message** (camelCase JSON). The message type is carried as a
**type discriminator** — the full type name (e.g. `"Orders.Commands.CreateOrder"`) — in
transport-native properties: HTTP `X-Conduit-Type` header, a SignalR hub method parameter, AMQP
`Type`/`Subject`, etc. The `DispatchEnvelope` wrapper is never serialized onto the wire.

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
`messageId` (self-causation). See [dispatch internals](dispatch-flows.md).

### Self-published event filter

A broker transport's check that drops an inbound event whose `conduit-source-app` header
(`ConduitHeaders.SourceApp`) names this host's own `ApplicationName`, so an application's listeners do
not run a second time on the copy of its own event that the broker routes back. See
[Transports → Self-published events](transports/index.md#self-published-events).

### Reply queue

A queue (broker transports) the sender owns for receiving request/response replies. Replies
**bypass** the topic/exchange and are delivered point-to-point to the reply queue. Reply queues are
created by `EnsureOutboundTopologyAsync` even in `ProducerOnly` mode.

### Jobs vocabulary

[Background Jobs](jobs.md) defines these in full.

- **Job** — background work declared with `conduit.AddJobs(...)` whose body is an ordinary command.
- **Schedule** — a code-declared job run on a timetable (cron or interval) or on demand; its name is unique.
- **Event trigger** — a job run when an event is published, built from the event.
- **Occurrence** — one moment a schedule is due, computed in memory from its declaration.
- **Run** — one execution of an occurrence or a trigger: a row in `conduit_job_runs`.
- **Claim** — the atomic update that elects one host to run a due run, renewed by a heartbeat while it runs.
- **Cursor** — a schedule's last completed occurrence, which stops a finished occurrence running again.
- **Misfire** — occurrences missed while the application was down: skipped, or one caught up.
- **Overlap** — whether a schedule's run may start while another is outstanding (refused by default).

---

See also: [Core Concepts](core-concepts.md) · [Dispatch Internals](dispatch-flows.md) ·
[Diagnostics](diagnostics.md) · [Configuration Reference](configuration-reference.md).
