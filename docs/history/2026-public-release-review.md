# The first public release review

> A record of the whole-library review Conduit went through before its first public preview (`1.0.0-preview.1`,
> packages `Conduit.NET*`): what it found, what was decided and why, and what shipped with known limits. Kept for the
> record; it is not maintained and is not part of the documentation site. The [guides](../../README.md#reading-order)
> describe the library as it ships, and [CHANGELOG.md](../../CHANGELOG.md) lists the user-visible changes.

Until this release Conduit had served one production application, a manufacturing ERP with an Angular front end. The
review read the whole library as a stranger installing it from nuget.org would, in a read-only review of every area
(core, source generator, TypeGen and the TypeScript runtime, HTTP and SignalR, brokers in two groups, persistence and
durability, audit, jobs and resilience, two documentation audits, release readiness), and held it to one bar: no
silent message loss, no trust in the wire by default, no generated code that fails to compile.

## What the review found

The core runtime's findings were narrow and are in the CHANGELOG: routes matched by raw string prefix, a relayed routed
`[FireAndForget]` command sent as request/response, unvalidated builder input.

### Broker transports

- **Shared behaviour was copied per transport, and the copies disagreed**: five reply correlators, with different
  timeout exceptions. Principle: what every broker must do lives once, in `BrokerTransportBase`.
- **Stopping closed channels, links and clients under running handlers**, losing their acknowledgements and replies.
  Principle: stop receiving, drain, then close.
- **Settlement was wrong both ways.** A message that failed before its handler (unreadable JSON, a DI failure) was
  acknowledged despite a dead-letter store, a handler's refusal (404, 409, 412) was dead-lettered, and a failed
  `[FireAndForget]` command was acknowledged and forgotten. Principle: dead-letter what never reached a handler or
  failed unexpectedly, never a refusal, and settle every message exactly once.
- **Ordinary operation lost messages or replies.** RabbitMQ's server-named reply queue changed name on recovery, so
  every later request timed out; SQS replicas deleted each other's replies from one shared queue; unfiltered ActiveMQ
  event queues ran pooled events twice and handed every application every other's events; Azure Service Bus settled
  with the processor's cancelled token on stop; abandon was an uncounted requeue (RabbitMQ `requeue: true`, ActiveMQ
  `Release`) or leaned on an SQS redrive policy never created, so a poison message looped hot. Principle: per-instance
  reply addresses, filtered queues, and an abandon the broker counts toward dead-lettering.
- **RabbitMQ and ActiveMQ published non-persistent messages**, so a Production broker restart lost what was in flight.
- **Resource names collided or were invalid**: truncation merged long sibling names, and SQS and SNS reject some
  characters. Principle: sanitize to `[A-Za-z0-9_-]`, hash the whole name when truncating, and reserve the pool names
  `replies` and `dead-letter`.

### Durability, jobs and audit

- **The inbox made the first transient failure final**: it recorded a `MessageId` before the handler and never removed
  it, so the redelivery was discarded as a duplicate.
- **The outbox relay had no backoff** (a one-minute broker outage failed every entry for good), resolved an entry by
  `Id` alone (a stale worker could resurrect a sent row), and disposed scopes synchronously. Principle: backoff with a
  retry API, and terminal writes guarded by the claim token.
- **Startup and schema were fragile.** The relay could start before its tables existed, concurrent hosts raced on
  `CREATE TABLE`, keys were unbounded unindexed text, and options went unvalidated (an audit `BatchSize` of zero spun a
  core). Principle: initializer first, concurrency-safe DDL, bounded indexed keys, options validated with ceilings.
- **Jobs could run twice, or when they should not.** Terminal writes ignored the claim token and the heartbeat never
  fenced itself; interval schedules anchored to each host's start, so replicas double-fired; a cancelled Pending run
  still ran; a `[FireAndForget]` body was recorded Completed before it ran. Principle: the claim token is the only
  authority, and every host computes the same occurrences.
- **Audit dropped records on shutdown**, recorded a long dispatch it evicted as stale as a failure, raced on an
  unsynchronized list, and wrote a batch one connection per record. Principle: drain with grace, evict on inactivity,
  and never record an outcome that did not happen.

### Resilience

- **`TransientErrors<T>()` retried refusals**: every remote 404, 409 and 412 (a `RemoteDispatchException`, which is an
  `InvalidOperationException`) and `JobOverlapException`. Principle: retry only what a retry can fix.
- **`AddPipelineForNamespace` registered a pipeline that nothing resolved.**
- **Pipelines were looked up by bare type and namespace names**, so one registered for something else could wrap a
  dispatch. Principle: Conduit owns its keys.
- **`RetryAttempted` was observed for failures never retried.** Principle: observe retries from Polly itself.

### HTTP and SignalR edges

- **The wire was trusted.** Caller-supplied message, conversation and causation ids were honoured from any caller,
  browsers included, so a browser could suppress a dispatch through the inbox, and a CR/LF id threw downstream.
  Principle: the server mints ids unless an edge opts in.
- **Server-mode SignalR pushed every non-`[LocalOnly]` event to every client.** Principle: push `[ClientVisible]` only.
- **Refusals and failures were misreported.** The type filter's refusal of an event was swallowed with an Error log; an
  unknown wire type answered 500 with a stack trace; a caller's cancellation became an `InternalError`; a failing event
  published over HTTP or SignalR answered success. Principle: a refusal reaches the caller with its status, an
  unexpected failure is `InternalError` with a reference, and nothing that never ran reports success.
- **The envelope and REST endpoints wrote with the host's JSON options**: enums as integers and closed sets as objects,
  unlike the generated TypeScript, and unreadable by the C# client. Principle: `ConduitRuntime.JsonOptions` throughout.
- **The API shape fought ASP.NET Core**: `.RequireAuthorization()` did not compile on `MapConduitEndpoint`'s return,
  bad REST input answered 500, a null command result 404, and `EndpointBuilder` collided with ASP.NET Core's type.
  Principle: route groups, 4xx for a caller's mistake, 204 for no result, and `ConduitEndpointBuilder`.

### Build and packaging

- **Generator tests never compiled the generated output**, so real shapes broke a consumer's build: generic messages,
  `internal` messages in referenced assemblies (CS0122), derived messages (CS8120), keyword-named types, a full type
  name two assemblies declare (a generator crash), and `ConduitRestEndpoints` emitted public by every assembly
  (CS0121). Principle: every generator test compiles its output, and an unsupported shape is a diagnostic.
- **TypeGen output was never type-checked**: same-named DTOs merged silently, closed generics duplicated declarations,
  an array result emitted `interface Item[]`, System.Text.Json attributes were ignored, and `Conduit.Sample.*` passed
  for framework. Principle: strict `tsc` over an emitted golden in CI; the emitter writes what the serializer writes.
- **The TypeGen task** was not incremental, wrote relative to the working directory and swallowed load failures, and
  Visual Studio's .NET Framework `msbuild.exe` cannot load a net10 task, so "dotnet CLI only" is documented.
- **Nothing was ready to publish**: no metadata, hard-coded ids, a version applied only at pack, internal names in
  shipped code and emitted TypeScript, an `InternalsVisibleTo` from the core to a feature package. Principle: one
  prefix property, metadata in `Directory.Build.props`, deterministic packs published only from a tag.

### Documentation

- **The docs claimed behaviour the code lacked** (the caller-id trust model before it existed, transport topologies,
  jobs claims, dead-letter fields, the HTTP status mapping). Principle: every claim checked against the code it names.
- **The guides assumed a reader who knew the one consumer**: internal names, usernames, dates and changelog narration,
  links into `src/**/CLAUDE.md`, a design spec describing unbuilt things beside them. Principle: user docs describe
  the library as it ships; maintainer material goes to `docs/contributing/`, the record to `docs/history/`.
- **Each transport guide had its own shape.** Principle: one skeleton for all six, Limitations included.
- **Nothing said why the library exists.** Principle: lead with the argument (`why-conduit.md`: one application, front
  and back, not two), then a reading order: Learn, Connect, Make it durable, Reference.
- **`security.md`, `configuration-reference.md` and `faq.md` were rewritten** rather than corrected.

## Decisions

### Release

- **Kafka dropped** (CONDUIT-47). Per-type queues, request/response with replies and competing consumers by type do
  not map onto partitions and consumer groups, and the review found the transport losing commands (one consumer group
  for every application) and replaying retained history on each start. The code is on `archive/kafka-transport`.
- **Packages are `Conduit.NET*`**: `Conduit` is taken on nuget.org and the `Conduit.` prefix is shared. One property,
  `ConduitPackagePrefix`, sets it; namespaces and assemblies stay `Conduit`. TypeGen packs its targets as
  `build/$(PackageId).targets` because NuGet imports only a targets file named after the package id.
- **MPL-2.0**: free use in commercial applications; changes to the library's own files stay open. The package
  `Copyright` names the individual author: the LICENSE carries no copyright line and no other holder was confirmed.
- **Linear history**: integrations are rebases, never merges. The release branch's two merges were rewritten as a line
  whose tree after each range equals the merged tree it replaced, so the public history reads and bisects as one.
- **Publishing is tag-gated.** The nuget.org push runs only on a `v*` tag behind the `release` environment, whose
  approval is the gate; the key travels through the environment with an empty-key guard, and `--skip-duplicate` lets a
  re-run finish a partial publish. The project and repository URLs are the only place the hosting account is named.
- **Infrastructure stays public but hidden.** Types other assemblies call (`TransportDispatcher`, `ConduitTypeRegistry`,
  `IWireMediator` and the like) are `[EditorBrowsable(Never)]`; `TypeFilterInterceptor` is internal.

### Wire and edges

- **The type filter gates events, and its refusal is answered**: a filter that refused an event while reporting
  success was an access-control hole and a silent loss. `/publish` answers the status-coded envelope, the hub throws
  `HubException`, a broker acknowledges (no caller waits). Broker-delivered messages are filtered too.
- **Server push is `[ClientVisible]`-only by default**: the attribute already names what the browser's generated client
  knows. A registered `IEventRecipientResolver` decides for every event (all, none, users, groups); a resolver that
  throws skips that push rather than failing the broadcast.
- **Caller ids are opt-in per edge.** The inbox dedups on `MessageId` and audit lineage follows the other two ids, so an
  untrusted caller's id suppresses dispatches or forges lineage. The server mints unless the edge opts in, and a
  trusting edge accepts the set only when every id matches `^[A-Za-z0-9-]{1,64}$`; REST never trusts. The C# clients
  send ids only when all are plain tokens, since a broker can hand them a foreign producer's.
- **An event body its type cannot read is a 400 at the edges and a dead-letter on a broker.** A silent 200 for a body
  that never ran was a defect; on a broker no caller is waiting, and contract drift is the likeliest unreadable event,
  so `BadRequest` and `ContractMismatch` events are captured while `Forbidden` is acknowledged.
- **No event failure is acknowledged silently** (CONDUIT-84). `HandleWireEvent` separates a listener failure, a
  refusal, a caller's cancellation and an infrastructure failure, the last wrapped as `EventDispatchException` (neither
  a `ConduitException` nor an `EventListenerException`) with its support reference. The edges answer it, and a
  listener failure, as `InternalError` with the reference (CONDUIT-88); a broker captures or abandons it.
- **`Validate()` refuses with `BadRequestException`.** An `ArgumentException` crosses a transport as a 500.
- **`RedeliverAsync` takes `RedeliveryIds?`.** A replay's ids come from the dead-letter entry, not the ambient
  `MessageContext`; HTTP and SignalR ignore them, since only brokers capture.

### Broker transports

- **One-shot dispatch, then dead-letter; no transport retry.** A transport dispatches once and settles, and abandon
  counts toward the broker's own redelivery limit. Application retry belongs in a pipeline behavior
  (`Conduit.Resilience`), where it is visible, per type and testable, not hidden inside four transports.
- **Persistent publishes everywhere** (CONDUIT-85). RabbitMQ sets `DeliveryMode` 2 and ActiveMQ `durable` on every
  message, replies included, so a durable Production queue keeps them across a broker restart; Azure Service Bus and
  SQS persist by nature. Losing messages on a broker restart was release-blocking.
- **The stop/drain contract.** `StopAsync` stops receiving, drains every in-flight `ProcessInboundMessageAsync`
  (acknowledgement included) within the host's shutdown token, closes the consumers, then fails pending replies. The
  shutdown timeout is the operator's deadline; `ResponseTimeout` measures a reply wait and never bounds the drain. A
  handler still running at the deadline is left behind, not cancelled; a delivery arriving during the drain goes back
  through `RequeueAsync`, not `AbandonAsync`, since it belongs to another instance and must not count as a failure.
- **A failed `[FireAndForget]` command is captured or abandoned** (CONDUIT-79). An `InternalError` goes to the inbound
  error interceptors when one is registered, otherwise to the broker's dead-lettering; `BadRequest` and
  `ContractMismatch` settle as unreadable; any other refusal is acknowledged with a Warning, since no caller waits.
- **One registration path for broker options**: `UseTransport<TTransport>(BrokerTransportOptions)` validates
  `ResponseTimeout` (positive, at most about 49.7 days, the timer limit past which every request fails), and every
  broker's `Use*` extension goes through it.
- **RabbitMQ's reply queue is exclusive, auto-delete and per instance.** Exclusivity keeps every other client on the
  vhost from consuming the instance's replies. When recovery meets `RESOURCE_LOCKED` (the broker still holds the
  half-open old connection), the transport mints a fresh instance id and reply address, logged once, and the requests
  already waiting time out through the correlator.
- **ActiveMQ.** A routed `[FireAndForget]` command goes to the anycast command address and a replay addresses its
  queue by FQQN (`address::queue`), or the per-type filters would drop them. A reconnect waits up to 10 s for the
  management link to close and bounds every step (shutdown waits at most 30 s for it): without the wait, the first
  request after a reconnect was lost in six runs of seven. The reply-sender cache, keyed by caller-supplied `ReplyTo`,
  holds at most 64 links, evicts only idle ones and closes them outside its lock.
- **Amazon SQS.** A subscription is created with its filter policy and raw delivery, and subscribed bare then updated
  only on SNS "already exists", so a new queue is never briefly unfiltered (a policy can take 15 minutes on AWS).
  Dead-letter queues keep 14 days; queue names are capped at 76 characters so `-dlq` fits SQS's 80; a pool named like
  a dead-letter queue fails the start. Configured `Credentials` apply with or without `ServiceUri` (CONDUIT-86); with
  `ServiceUri` and no `Credentials` the client keeps a placeholder pair for LocalStack.

### Durability and persistence

- **The inbox releases a failed dispatch**: a record that outlived a failed handler made the first transient failure a
  loss and discarded a replay carrying the original `MessageId`. Only the record this dispatch made is released, with
  or without a dead-letter capture; a crash between recording and completing is still at-most-once.
- **`MarkSent` is the point of no return.** In the relay and the flush, the entry's scope is disposed after it, outside
  the per-entry catch, and a dispose failure is a Warning, never a retry that would send a sent message again.
- **Terminal writes match `Status = Claiming AND ClaimToken = @ClaimToken` and count only when one row changed**, in
  the outbox and in jobs, so a second resolution under the same claim cannot flip a sent row. A SQL Server session
  with `NOCOUNT ON` reports -1 rows and defeats this; Conduit's own connections leave it off.
- **`RetryFailedAsync(long id)`**: the entry key is a `long`, and an id overload and a message-type overload cannot
  both take a `string`. Backoff options are code-only, like all `OutboxOptions`; a retried row keeps `CreatedAt`.
- **DDL is idempotent per statement.** `BuildCreateTableIfNotExistsSql` takes the indexes (MySQL has no idempotent
  `CREATE INDEX`), and `DbStoreBase.EnsureCreatedAsync` tolerates a concurrent create per statement: re-running a
  whole initializer still failed on Postgres (23505) and SQL Server (1913, 3961) with eight racing hosts. SQL Server
  runs each statement at READ COMMITTED under `sp_getapplock`, since `CREATE INDEX` deadlocked (1205). Startup order
  relies on sequential hosted-service start; `ServicesStartConcurrently` is unsupported.
- **A dead-letter replay is claimed**, at READ COMMITTED (`ISqlDialect.BuildReadCommittedSql`) so a Snapshot session
  loses the claim instead of failing with 3960, for an internal 5 minutes rather than an option inviting the wrong
  knob. A replay whose removal fails reports success and logs `DlqReplayedEntryNotRemoved`: the message went out.
- **Schema changes carried no migration.** Conduit was unreleased; a table an internal build created is dropped, as
  the [pre-release upgrade notes](pre-release-upgrade-notes.md) say.

### Jobs

- **Interval schedules align to the Unix epoch**: occurrences are whole multiples of the interval since it, so every
  host computes the same ones without a cursor; `Every(7 days)` fires on Thursdays at 00:00 UTC. `NextOccurrenceAfter`
  is strictly after its argument, as cron's is, since every caller passes an occurrence that just fired.
- **`Run<TCommand>` is generic**, so a `[FireAndForget]` body, which would be recorded Completed before it ran, is
  refused where the job is declared.
- **Options fail fast, with ceilings.** `JobsOptions.Validate()` throws `InvalidOperationException` at the first
  failure (the package's convention); `MaxRuntime` is per schedule, checked in `AddJobs`; durations are capped (one
  day for `CleanupInterval` and `RetryBackoff`, 3650 days for retentions), as a timer past about 49.7 days never fires.
- **One wake signal**, a `TaskCompletionSource`-based singleton shared by `DurableJobs` and the scheduler, because a
  manage-only host cannot reach the hosted service. Trigger-now no longer waits for the next sweep.

### Audit

- **Provisional records.** A dispatch silent past `StaleGroupTimeout` is still awaiting, not lost: its group is stored
  with a non-terminal `Conduit.Audit.StaleGroupEviction` marker, and a late terminal observation seals it and writes
  the real record. A longer timeout still lies past its ceiling, and a correcting record alone leaves the false
  failure stored. Two records per long dispatch is the cost; `GetByMessageIdAsync` prefers the latest real one.
- **`MaxPayloadBytes` of zero or less means no cap**; an enabled cap below 1 KB is refused at `AddAudit`.
- **Stop order needed no change**: `AddAudit` registers before the background dispatcher and hosts stop in reverse, so
  audit drains after it; a hosted service registered before `AddAudit` stops after the publisher, a documented limit.

### Resilience

- **Namespaced pipeline keys.** Conduit looks up `Conduit.Type:{full name}`, `Conduit.Namespace:{namespace}` and
  `Conduit.Default` and nothing else, so a pipeline an application registers for its own use never wraps a dispatch.
  A typed pipeline (`ResiliencePipeline<TResult>`, for `HandleResult`) is registered by the host under the same keys.
- **Transient means a timeout or a remote `InternalError`.** `RemoteDispatchException` gained `Code` so a refusal can
  be told from a failure. `HttpRequestException` stays unhandled; a host that wants it adds a predicate.
- **Retries are observed through a Polly `TelemetryListener`**, not `OnRetry`: the host owns its
  `RetryStrategyOptions`, and a listener is the one hook Conduit controls. It fires only for a real retry.

### Documentation

- **The site is built from `docs/` as it stands**: a prebuild script copies it into Starlight, derives each `title:`
  from the first heading, strips the hand footers and rewrites links, so `docs/` stays plain Markdown for GitHub.
  `why-conduit.md` is the landing page; `docs/contributing/` and `docs/history/` are left out.

## Known limitations at release

- **Resilience pipelines never see listener failures** (CONDUIT-82). `PublishLocalAsync` throws the
  `EventListenerException` after the behaviors have returned, so Polly never retries a listener that threw; a listener
  whose construction fails is retried, re-running every listener. `resilience.md` states it.
- **In-memory jobs ignore `MaxRuntime` on a manual run** (CONDUIT-83): an `InMemoryJobs.TriggerNowAsync` run is not
  cancelled at its limit. The durable scheduler honours it.
- **ActiveMQ addresses are not auto-deleted.** Artemis reaps only addresses it auto-created, and `createAddress`
  through management has no auto-delete, so each Developer or Test prefix leaves two empty addresses behind. Queues do
  auto-delete. The cost is broker clutter in development and test, never data.
- **SQS has no auto-delete.** Developer and Test resources, their dead-letter queues, and the reply queue of an
  instance that ended without stopping remain until deleted. `AmazonSqsTopologyReaper.ReapByPrefixAsync` deletes by
  prefix, and each of Conduit's SQS test projects reaps its own `test-{hash8}-` prefix once. Production has no reaper.
- **Real-AWS behaviour is unverified.** LocalStack never refuses a changed filter policy or queue attribute and does
  not model propagation delays, so the SNS "already exists" fallback and the attribute updates are tested only against
  it, though they run whenever a release changes a queue's types. A one-time real-account probe was recommended.
- **The inbox beyond the built-in edges.** A trusting edge (`MapConduitEndpoint(trustCallerContext: true)`, a hub with
  `TrustCallerContext`) releases a failed dispatch's inbox record as a broker transport does, through
  `IInboundFailureObserver`: a request answered `InternalError`, or an event whose listener throws or whose dispatch
  fails outside its listeners, so a caller's retry under the same `MessageId` (the outbox relay's next attempt) runs; a
  refusal stays recorded. Dead-letter capture stays broker-only. Code that calls `IWireMediator.HandleWireRequestAsync`
  under a context it started releases nothing unless it reports the failure to every `IInboundFailureObserver` as the
  built-in edges do; `inbox.md` states both.
- **No broker or database test runs in CI**, and the tag-gated publish runs none, so a release is proven by running the
  container suite locally on the tagged commit.

## What was deliberately not done

- OpenAPI request and response metadata on `MapConduit*`: additive, and nothing in the preview needed it.
- TypeGen multi-targeting for Visual Studio's `msbuild.exe`: documented as dotnet CLI only instead.
- Event receive on the C# client-mode `SignalRTransport`: documented as send-only; the browser client receives pushes.
- A RabbitMQ channel pool: a dedicated reply channel and publisher confirms fixed the defect; a pool is throughput.
- Receiver-pool routing for `RedeliverAsync`: a replay keeps its ids, and the pool is an optimization.
- Recipient policies beyond `IEventRecipientResolver`: the hook exists; policy is the host's.
- `TopologyNaming` sanitization beyond `[A-Za-z0-9_-]` and the whole-name hash: enough for every supported broker.
- Distinct names for the per-transport `EndpointBuilderExtensions` classes: no caller spells them.
- A typed builder overload for resilience pipelines: additive later, and the key strings are documented.
- A configurable dead-letter replay claim timeout: no caller asked for one.
- A visibility audit of the feature packages' stores, relays and schemas: acceptable in a preview, due before 1.0.
- Broker tests on hosted CI runners: to be revisited on a self-hosted runner.

## Where things went

- The original design spec, `docs/design.md`, is [2026-design.md](2026-design.md), bannered with what was never built.
- The transport test matrices are in [contributing/](../contributing/transport-test-matrices.md).
- The configuration reference's "Upgrading an existing host" is
  [pre-release-upgrade-notes.md](pre-release-upgrade-notes.md): the first public release has no upgrading hosts.
- The FAQ's Task-versus-ValueTask essay is in [design-notes.md](design-notes.md).
- The "Why Conduit" essay is [why-conduit.md](../why-conduit.md), the site's landing page; the README leads with it.
- The review's working spec, plan, rulings and final review were working files; this page replaces them.
