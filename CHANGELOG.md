# Changelog

All notable changes to Conduit are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## 1.0.0-preview.1 — Unreleased

The first public preview. The entries below are the changes since the earlier, unpublished builds.

### Known issues

- Resilience pipelines never see an event listener's failure (CONDUIT-82): a listener's exception is thrown, with the
  others', as an `EventListenerException` after the pipeline has returned, so `Conduit.NET.Resilience` never retries a
  listener that threw.
- Without a database, a job run started by hand (`IJobs.TriggerNowAsync`) ignores the declaration's `MaxRuntime`
  (CONDUIT-83); scheduled and triggered runs honour it.

### Added

- `IInboundFailureObserver` (`Conduit.Runtime`): the HTTP wire endpoint and the SignalR hub tell every registered
  observer of a dispatch that failed unexpectedly (an `InternalError` answer, a listener's failure, a failure outside
  the listeners) before they answer it. Never a refusal.
- `IOutbox` (`Conduit.Durability.Outbox`), registered by `AddOutbox`: `QueryFailedAsync` lists the entries whose
  retries ran out and `RetryFailedAsync` hands one, or every one of a type, back to the relay.
- The outbox relay backs off exponentially between attempts (`OutboxOptions.InitialBackoff`, 5 s, doubling up to
  `MaxBackoff`, 5 min); a failed entry used to be due again on the very next sweep.
- `RemoteDispatchException.Code`: the remote `ErrorCode`, with its HTTP status.
- Amazon SQS: every application and pool queue has a `{queue}-dlq` dead-letter queue and a redrive policy, with
  `AmazonSqsOptions.MaxReceiveCount` (default 5).
- Audit: `ShutdownGrace` (default 5 s) bounds the drain when the host stops.

### Removed

- The Kafka transport (`Conduit.Transport.Kafka`).

### Changed

- The dispatch envelope leaves an absent member off the wire instead of sending null: `error` on a success, and an
  error's `details`, `type`, `data` and `reference`, and a detail's `field`, when they are empty. The TypeScript client's
  `DispatchResult.error`, `ConduitError` and `ConduitErrorDetail` lose their `| null`. `data` is always written, so a
  `T?` handler's null still arrives as null. A C# client reads an absent member as null, as before.
- Packages publish under the `Conduit.NET` prefix (`Conduit.NET`, `Conduit.NET.SourceGen`, `Conduit.NET.Transport.Http`
  and so on); namespaces and assembly names stay `Conduit`.
- `EndpointBuilder` is renamed `ConduitEndpointBuilder`; the old name collided with ASP.NET Core's `EndpointBuilder`.
- The SignalR hub pushes only `[ClientVisible]` events to its clients by default; it pushed every broadcast event.
  Register an `IEventRecipientResolver` to decide otherwise: it then chooses the recipients of every event
  (`EventRecipients.All`, `None`, `Users` or `Groups`).
- Caller-supplied message, conversation and causation ids are honoured only where the edge opts in:
  `MapConduitEndpoint(trustCallerContext: true)` over HTTP, `ConduitSignalROptions.TrustCallerContext` on the hub.
  Otherwise the server mints them, and even a trusting edge accepts them only as plain tokens
  (`MessageContextIds.IsValid`).
- The HTTP envelope endpoint reads client properties (`X-Conduit-Property-*`) only when mapped with
  `readClientProperties: true`.
- `MapConduitEndpoint` returns the `RouteGroupBuilder` it maps `/dispatch` and `/publish` in, so a convention chained
  on it applies to both.
- `ConduitHub` is sealed. A subclass never received server push (the transport pushes through
  `IHubContext<ConduitHub>`); map `ConduitHub` itself and authorize the mapping.
- `ConduitPredicates.TransientErrors<T>()` handles only a `TimeoutException` and a remote `InternalError`. It no longer
  handles `InvalidOperationException`, which every remote refusal (404, 409, 412) was.
- Resilience pipeline keys are `Conduit.Type:{full name}`, `Conduit.Namespace:{namespace}` and `Conduit.Default`. A
  pipeline registered by hand under a bare type name or namespace is no longer used.
- `IConduitTransport.RedeliverAsync` takes a `RedeliveryIds?`, so a dead-letter replay keeps the original message,
  causation and conversation ids.
- An event whose body its type cannot read is refused: HTTP `/publish` answers 400 and the hub's `Publish` throws a
  `HubException`, where both reported success.
- An event published over HTTP or SignalR whose dispatch fails (a pipeline behavior, listener resolution, a gate, or a
  listener) is answered as an unexpected failure, its exception logged under a support reference: `/publish` answers
  `InternalError` with the reference (a `RemoteDispatchException` at the C# `HttpTransport`), and the hub's `Publish`
  throws a `HubException` with the fixed message. Every such failure but a listener's was reported as success, so an
  outbox relaying through `HttpTransport` now retries what it used to mark delivered.
- The outbox and dead-letter tables changed columns. Drop the `conduit_outbox` and `conduit_dead_letters` tables an
  earlier build created; Conduit creates the current ones at startup.
- RabbitMQ topology: each transport instance has its own client-named reply queue, and every inbound queue
  dead-letters to one `{system}-dead-letter` exchange per system. A durable queue declared by an earlier build fails
  the declaration (`PRECONDITION_FAILED`); delete it.
- ActiveMQ topology: event queues are filtered to the event types their consumers subscribe to, and each connection
  has its own temporary reply queue.
- **Jobs: interval schedules align to the Unix epoch.** An `Every(...)` schedule's occurrences fall at
  `UnixEpoch + k × interval`, so replicas started at different times agree on them; they used to count from each
  host's start. This moves when they fire: `Every(TimeSpan.FromHours(1))` fires on the hour, and
  `Every(TimeSpan.FromDays(7))` on Thursdays at 00:00 UTC (1 January 1970 was a Thursday).
- Jobs: `Run<TCommand>(...)` is generic, and a `[FireAndForget]` command is refused as a job body (its handler would
  run after the run was recorded complete).
- Jobs, the outbox, the inbox and the audit trail validate their options when registered and throw, naming the
  option, for a value out of range (a duration not positive or above one day, a retention above 3650 days, and so
  on).
- `ResponseTimeout` is validated when a broker endpoint is registered: positive and at most about 49.7 days.
- The pool names `replies` and `dead-letter` are reserved; `AddPool` throws for them.
- RabbitMQ: `Concurrency` is real dispatch concurrency. Handlers run in parallel up to `DefaultConcurrency` (10),
  where it used to set the prefetch alone and messages ran one at a time.
- RabbitMQ: every publish waits for the broker's confirm, and every message is persistent.
- ActiveMQ: messages are durable, and a failed delivery is settled `modified` with `delivery-failed`, which Artemis
  counts, so `max-delivery-attempts` moves it to the dead-letter address.
- Amazon SQS: each transport instance has its own reply queue, deleted when it stops. The identity the transport runs
  as needs the actions the guide's Permissions section lists, among them `sqs:DeleteQueue`,
  `sqs:ChangeMessageVisibility`, `sqs:GetQueueAttributes`, `sqs:SetQueueAttributes` and
  `sns:SetSubscriptionAttributes`.
- `OutboxEntry` and `OutboxEntryStatus` moved to `Conduit.Durability.Outbox`, beside `IOutbox`. `OutboxOperations`
  and `DeadLetters` are internal: inject `IOutbox` and `IDeadLetters`.
- Infrastructure types a host never names are hidden from IntelliSense, and `TypeFilterInterceptor` is internal
  (`AddTypeFilter` registers it).
- `ConduitHub`'s constructor takes an `ILogger<ConduitHub>`.
- A REST command whose handler returns `null` answers 204, not 404.
- The dead-letter queue logs under EventIds 311-314 (`DlqReplaySucceeded`, `DlqReplayFailed`,
  `DlqReplayedEntryNotRemoved`, `DlqReplayClaimNotReleased`), apart from the background dispatcher's 300-303.

### Fixed

- **Source generator**
  - Generic, internal, derived and keyword-named messages produce compilable code; a generic message (CONDUIT007) and
    a full type name two assemblies declare (CONDUIT008) are build errors.
  - `[LocalOnly]` and `[FireAndForget]` are inherited, and the broadcast gate fails closed for an event type the
    generator never saw.
  - A listener of a message the generated code cannot name is skipped instead of breaking the build.
  - The type registry knows every event, and a listener registered at runtime activates its event.
  - The generated REST endpoints are internal, diagnostics point at the declaration, and `[RemoteContracts]` matches
    whole namespace segments.
  - Moving a declaration no longer re-emits the generated code.
- **Runtime and routing**
  - A refusal of an inbound event reaches its publisher; an unknown message type is a 400, and a caller's
    cancellation is not a server error.
  - `RouteType` matches the exact type, and `RouteNamespace` stops at the namespace boundary.
  - Builder input is validated, exception constructors reject null, and a serializer failure restores the caller's
    context.
- **Outbox**
  - A relayed event broadcasts to every transport instead of following a route.
  - A relayed routed `[FireAndForget]` command is sent without waiting for a reply.
  - Resolving an entry (sent, retry, failed) is guarded by the token of the claim that took it, so a worker whose
    claim went stale cannot release or overwrite a row another worker has since claimed and sent.
  - A claim cancelled part-way (a relay stopping with the host, a flush whose caller gave up) hands back the entries
    it had just claimed, rather than leaving them undelivered until `ClaimTimeout`.
  - The tables are created before the relay and the inbox cleanup make their first pass, the outbox's queries have
    indexes, and a second `AddOutbox` or `AddInbox` (another module's) registers nothing twice.
- **Inbox**
  - A failed dispatch releases its inbox record, so the broker's redelivery or a dead-letter replay runs instead of
    being discarded as a duplicate.
  - So does an unexpected failure behind an HTTP endpoint mapped with `trustCallerContext: true` or a hub with
    `TrustCallerContext` (CONDUIT-80): a retry under the same `MessageId` (the outbox relay's next attempt, a resend of the same
    request) was answered as an empty success and the message lost.
- **Dead-letter queue and persistence**
  - A replay claims its entry first, so two concurrent replays of one entry publish it once.
  - Concurrent table creation by replicas starting together succeeds; on SQL Server it runs under `sp_getapplock`.
  - The dead-letter filters compare instants in UTC, a connection that fails to open is not leaked, and every store
    takes its clock from an injectable `TimeProvider`.
  - On SQL Server every connection Conduit opens runs `SET NOCOUNT OFF`, so a server whose `user options` include
    `NOCOUNT` no longer makes the outbox resend sent entries and jobs run again.
- **Jobs**
  - A run's terminal write is guarded by its claim token, and a heartbeat that cannot renew its claim cancels the run
    once the claim could be taken over, so a stalled host's late outcome never lands on another host's run.
  - A run cancelled before it started is never dispatched.
  - Stopping drains running jobs only until the host's shutdown deadline.
  - A sweep that a host's stop cancels part-way hands back the runs it had just claimed, rather than leaving them
    claimed by nobody until `ClaimTimeout`.
  - A delayed trigger's run, a coalescing scope or a retry that comes due while the sweep is mid-pass is taken at
    once, rather than up to `ClaimTimeout` later.
  - A stopping host takes no new work from the moment it begins to stop: the scheduler stops claiming, where it used
    to re-claim a just-drained run and restart its handler under a cancelled token hundreds of times, and a run an
    event trigger writes during shutdown is handed back unstarted.
  - `TriggerNowAsync` wakes the sweep, so a manual run starts at once rather than on the sweep's next pass.
- **Audit**
  - Stopping drains the queued records for `ShutdownGrace` at most, and counts what it abandons.
  - A dispatch silent past `StaleGroupTimeout` gets a provisional record, then its real one; it used to be recorded
    as failed and its real outcome dropped.
  - A batch is written in one transaction, so a failing record keeps none of it.
- **HTTP and SignalR**
  - The envelope and REST endpoints serialize with Conduit's JSON options, never the host's.
  - Bad REST input is answered 4xx, and a multipart body binds only what a JSON body could.
  - Client property names are case-insensitive.
  - The C# `HttpTransport` reports a non-JSON failure as its HTTP status and sends a routed fire-and-forget command
    without waiting for its reply.
  - The C# SignalR client reconnects after a connection closed for good, is disposed with the container, and takes
    connection options.
  - The C# clients forward correlation ids only when they are plain tokens.
  - A failing `IEventRecipientResolver` skips that push instead of failing the broadcast.
  - Calling `AddConduitSignalR` twice (two modules of one host) registers the server transport once; every event was
    pushed to every browser twice (CONDUIT-87).
- **TypeScript generation**
  - Valid TypeScript for same-named DTOs, generic wrappers, arrays, more BCL types and System.Text.Json attributes.
  - A property that is always ignored (`[JsonIgnore]`) is not walked for type discovery.
  - The TypeScript runtime reports transport failures, waits through a SignalR reconnect, and carries properties over
    HTTP; its `HttpTransport` never sends caller-supplied correlation ids.
  - The TypeGen target is incremental, its output path is project-relative, and assembly load failures are logged.
- **Broker transports**
  - A request with no reply fails with a `TimeoutException` on every broker; RabbitMQ and ActiveMQ raised a
    `TaskCanceledException`.
  - Stopping drains in-flight handlers before channels, links and clients close.
  - A message that cannot be dispatched, including an event whose body cannot be read, is dead-lettered; a handler's
    refusal is not, and a capture is settled once.
  - Resource names are sanitized to `[A-Za-z0-9_-]`, and a truncated name hashes the whole name, so long sibling names
    no longer collide.
  - The Amazon SQS topology reaper matches the sanitized prefix.
  - A `[FireAndForget]` command whose handler fails is captured or abandoned, never acknowledged; one that is refused
    is acknowledged with a Warning (EventId 511).
  - An event whose dispatch fails outside its listeners (a pipeline behavior, a listener that cannot be resolved, a
    gate that throws) is captured or abandoned, never acknowledged as handled.
  - A request's publish is bounded by `ResponseTimeout`, so a publish the broker never confirms fails the request with
    the same `TimeoutException` as a missing reply.
  - A message delivered while the transport is stopping is requeued for another instance, not abandoned as a failure.
  - A request whose mediator itself throws is answered `InternalError` with the fixed message and a support reference,
    not the exception's message.
  - RabbitMQ: a reply queue another connection still holds (`RESOURCE_LOCKED`) is replaced by one under a fresh name.
  - Azure Service Bus: a message is settled on shutdown without the cancelled token; a reply, and the reply queue,
    carry a time to live, so replies nobody reads expire; a pool assignment that changes updates the application's
    own subscriptions.
  - ActiveMQ: the transport reconnects after a lost connection, Developer and Test queues really auto-delete, the
    cached reply senders are capped at 64, and an exception escaping an inbound dispatch is logged (EventId 113)
    instead of becoming an unobserved task exception.
  - Amazon SQS: queue attributes and subscription filter policies are updated idempotently at every start, and
    configured `Credentials` apply with or without a custom `ServiceUri`.
- **Resilience**
  - A namespace pipeline (`AddPipelineForNamespace`) is resolved.
  - `RetryAttempted` is observed only for real retries, with the delay, and never carries a retried result's values.
