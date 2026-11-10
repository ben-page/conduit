# Changelog

All notable changes to Conduit are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## 1.0.0-preview.1 — Unreleased

The first public preview. The entries below are the changes since the earlier, unpublished builds.

### Removed

- The Kafka transport (`Conduit.Transport.Kafka`).

### Changed

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
- `ConduitPredicates.TransientErrors<T>()` handles only a `TimeoutException` and a remote `InternalError`. It no longer
  handles `InvalidOperationException`, which every remote refusal (404, 409, 412) was.
- Resilience pipeline keys are `Conduit.Type:{full name}`, `Conduit.Namespace:{namespace}` and `Conduit.Default`. A
  pipeline registered by hand under a bare type name or namespace is no longer used.
- `IConduitTransport.RedeliverAsync` takes a `RedeliveryIds?`, so a dead-letter replay keeps the original message,
  causation and conversation ids.
- An event whose body its type cannot read is refused: HTTP `/publish` answers 400 and the hub's `Publish` throws a
  `HubException`, where both reported success.
- The outbox and dead-letter tables changed columns. Drop the `conduit_outbox` and `conduit_dead_letters` tables an
  earlier build created; Conduit creates the current ones at startup.
- RabbitMQ topology: each transport instance has its own client-named reply queue, and every inbound queue
  dead-letters to one `{system}-dead-letter` exchange per system. A durable queue declared by an earlier build fails
  the declaration (`PRECONDITION_FAILED`); delete it.
- ActiveMQ topology: event queues are filtered to the event types their consumers subscribe to, and each connection
  has its own temporary reply queue.

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
- **Resilience**
  - A namespace pipeline (`AddPipelineForNamespace`) is resolved.
  - `RetryAttempted` is observed only for real retries, with the delay, and never carries a retried result's values.
