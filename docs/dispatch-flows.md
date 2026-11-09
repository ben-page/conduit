# Dispatch Flows

End-to-end traces of how a message moves through Conduit, using the real type and
method names from the source-generated mediator (`ConduitMediator`) and the runtime
helpers in `Conduit.Runtime`. These are the canonical reference for understanding the
dispatch pipeline without reading every line of the generator and runtime.

Three flows are covered:

- [Local command / query dispatch](#1-local-command--query-dispatch)
- [Event broadcast](#2-event-broadcast)
- [Wire-inbound message](#3-wire-inbound-message)

The generated `ConduitMediator` implements `IWireMediator : IMediator` and is registered
as **transient** — it is stateless and resolves handlers from `IServiceProvider` per call.
Handlers and listeners are also **transient**. This is what lets `IMediator` be injected
into singletons such as a `BackgroundService`.

---

## 1. Local command / query dispatch

Caller: `await mediator.ExecuteAsync(command, ct)` (or `FetchAsync(query, ct)` for queries).
The generated body of `ConduitMediator.ExecuteAsync` runs the following sequence.

```
IMediator.ExecuteAsync(command)
  |
  1. MessageContext.StartDispatch(options)        -- new MessageId; inherits ConversationId,
  |                                                  sets CausationId = parent.MessageId
  |                                                  (head/origin self-causates: CausationId = MessageId)
  |                                                  (payload captured for audit if enabled)
  2. ConduitRuntime.ResolveRoute(options, "Ns.MyCommand")
  |        |
  |        +-- endpoint != null  ->  ROUTED (remote)
  |        |       ConduitRuntime.ResolveTransport(serviceProvider, endpoint)   -- keyed IConduitTransport
  |        |       JsonSerializer.SerializeToElement(command)  ->  DispatchEnvelope("Ns.MyCommand", payload)
  |        |       transport.SendRequestAsync(envelope, ct)    -- request/response over the wire
  |        |       deserialize reply -> return TResult
  |        |
  |        +-- endpoint == null  ->  LOCAL
  |                ConduitDispatch.Execute(command, serviceProvider, conduitOptions, ct)
  |                    ConduitRuntime.DispatchAttemptsAsync(serviceProvider, attempt, ct)   -- the attempt boundary
  |                    for each attempt (one, on serviceProvider, unless a retry policy covers the type):
  |                      a. handler = attemptServices.GetRequiredService<ICommandHandler<MyCommand, TResult>>()
  |                      b. behaviors = ConduitRuntime.ResolvePipelines<MyCommand, TResult>(options, attemptServices)
  |                      c. ConduitRuntime.ExecuteWithBehaviors(command, behaviors, handlerCall, ct)
  |                           -> behavior[0] ( behavior[1] ( ... ( handler.HandleAsync ) ) )
  3. MessageContext.RestoreContext(previous)       -- in finally
```

Key facts verified against the code:

- **Routing decision** is `ConduitRuntime.ResolveRoute` (`ConduitRuntime.cs`). It scans
  `options.Routes`, matching `messageFullTypeName.StartsWith(route.Pattern)`. A match with a
  missing endpoint throws `InvalidOperationException`. No match returns `null` → local dispatch.
- **Routed dispatch** for commands with a result and queries uses `transport.SendRequestAsync`.
  Non-fire-and-forget void commands also use `SendRequestAsync` (to confirm handler execution);
  `[FireAndForget]` void commands use `SendEventAsync` (no reply). See
  [fire-and-forget](#fire-and-forget-void-commands) below.
- **Pipeline ordering** is owned by Conduit, not DI. `ResolvePipelines` constructs closed
  generics from `options.PipelineTypes` in registration order. `ExecuteWithBehaviors` wraps
  them so the **first registered behavior is the outermost** (it iterates from the last index
  inward when building the `next` chain).
- **The attempt boundary** is `ConduitRuntime.DispatchAttemptsAsync`. The generated `Execute`, `Fetch` and
  `ExecuteVoidWire` hand it the old body (`ExecuteAttempt`, `FetchAttempt`, `ExecuteVoidWireAttempt`) as a
  delegate over an `IServiceProvider`. With no `IDispatchRetryPolicy` registered, with a policy that does not
  cover the type, or for a dispatch made from the running attempt's own scope, it runs the delegate once on
  `serviceProvider` itself, so the dispatch behaves as if the body were inline. Otherwise it runs each attempt in
  a fresh scope from the root `IServiceScopeFactory` and retries while the policy gives a reason. The wire path
  (`DispatchRequestAsync`) calls the same `ConduitDispatch` methods, so it gets the same boundary. Step 1's
  context start also calls `ConduitRuntime.BindDispatchAttempt`, which records on the new `MessageContext` the
  attempt that owns it (set only when `serviceProvider` is that attempt's scope). See
  [Dispatch Retry](dispatch-retry.md).
- **No local handler and no route** → the generated code throws
  `InvalidOperationException("No local handler for Ns.MyCommand and no route matched.")`.

### Fire-and-forget void commands

For a local `[FireAndForget]` void command, `ConduitDispatch.Execute` resolves the pipeline
behaviors in the **caller** context, then enqueues the handler invocation to the
`ConduitBackgroundDispatcher` (a `BackgroundService` with an unbounded `Channel<T>`) via
`dispatcher.Enqueue(...)`, and returns immediately. This path never involves the outbox — there's
no transport send to defer, only a local handler invocation. It also sits outside the
[attempt boundary](dispatch-retry.md#which-dispatches-are-covered): it runs no handler in the caller's
flow, so `Execute` here is not routed through `DispatchAttemptsAsync`. The same command arriving over
the wire runs through `ExecuteVoidWire`, which runs the handler inline and does use the boundary.

For a **routed**, non-Immediate `[FireAndForget]` command, the generated mediator first checks for
an `IOutboundInterceptor` (outbox capture) and offers it the envelope. `Intercept` returns `bool`:
`true` (captured — requires an ambient transaction) replaces the transport call entirely, and the
outbox delivers it later; `false` (declined, or no interceptor registered) falls through to
`transport.SendEventAsync(envelope, ct)` exactly as if the outbox didn't exist.
`[FireAndForget(Immediate = true)]` routed commands skip the interceptor check altogether — always
straight to `transport.SendEventAsync`. See [Outbox → What gets captured](outbox.md#what-gets-captured).

---

## 2. Event broadcast

Caller: `await mediator.PublishAsync(@event, ct)`. Events do **not** route — they broadcast.

```
IMediator.PublishAsync(@event)
  |
  1. MessageContext.StartDispatch(options)
  2. local delivery:
  |     await PublishLocalAsync(@event, ct)         -- private; local listeners only;
  |                                                 -- EventListenerException propagates to the caller
  3. transport broadcast:
  |     foreach IConduitTransport in DI:
  |         transport.SendEventAsync(DispatchEnvelope("Ns.MyEvent", payload), ct)
  4. MessageContext.RestoreContext(previous)
```

`PublishLocalAsync` (generated, private) dispatches to every local `IEventListener<MyEvent>`
with **continue-on-failure** semantics:

```
PublishLocalAsync(@event)
  |
  +-- foreach listener:
  |       behaviors = ConduitRuntime.ResolvePipelines<MyEvent, object>(options, sp)
  |       try
  |           await ConduitRuntime.ExecuteWithBehaviors(@event, behaviors, listenerChain, ct)
  |       catch (Exception ex)
  |           ConduitRuntime.LogListenerFailure(...)   -- record, keep going
  |
  +-- if any listener threw:
          throw new Conduit.Exceptions.EventListenerException(...)
```

Key facts:

- **All listeners run even if some throw.** Failures are collected; `EventListenerException`
  is thrown only after every listener has been attempted.
- **`[LocalOnly]` events skip transport broadcast** — the generator emits a gate that lists, by full
  type name, every event it discovered that is not `[LocalOnly]` (on itself or a base: the attribute
  is inherited). Any other runtime type, one the generator never saw included, is not broadcast.
- **`[FireAndForget]` events (non-Immediate)** are offered to a registered `IOutboundInterceptor`
  (the outbox) *before* any local dispatch. When it captures the envelope — `Intercept` returns
  `true`, which requires an ambient DB transaction — the entire publish body is skipped: local
  listeners and transport broadcast both happen later, from the outbox, post-commit. When capture
  is declined (no transaction) or no interceptor is registered, the local-listener work is
  enqueued to `ConduitBackgroundDispatcher` (`dispatcher.Enqueue(...)` wrapping `PublishLocalAsync`)
  instead of running it inline, and transport broadcast still proceeds inline (events are already
  fire-and-forget on the wire). `[FireAndForget(Immediate = true)]` events skip the interceptor
  check entirely — always background-enqueued locally and broadcast directly, so delivery doesn't
  wait on (or depend on) an enclosing transaction. See
  [Outbox → What gets captured](outbox.md#what-gets-captured) for the full matrix.
- **Broadcast itself is capture-gated too**, for every event that reaches it (plain events always
  do; F&F events only after a declined/absent pre-check above, never a second attempt). The
  broadcast step offers the envelope to the interceptor and only calls `transport.SendEventAsync`
  directly when nothing captured it — so a plain event's transport delivery can also be deferred
  to the outbox, even though its local listeners already ran inline, above.
- **A listener failure fails the publish.** `PublishAsync` runs a plain event's listeners inline and
  lets `EventListenerException` propagate to the publisher, as the **wire path** (`DispatchEventAsync`,
  see below) does for the transport to retry / dead-letter. Its `InnerException` is the first listener
  failure in run order, so `GetBaseException()` reaches the root cause. A failed publish skips the
  transport broadcast that follows the local listeners. The exception is a `[FireAndForget]` event: its
  listeners run in the background after the publisher has moved on, so `ConduitBackgroundDispatcher`'s
  work item logs the `EventListenerException` and nothing is thrown.

---

## 3. Wire-inbound message

A message arriving from another process passes through the **three-layer middleware model**.
This trace uses a broker transport (`BrokerTransportBase<TMessage>.ProcessInboundMessageAsync`);
HTTP (`MapConduitEndpoint`) and SignalR (`ConduitHub`) follow the same `IWireMediator` calls.

```
transport receives raw message
  |
  1. messageType = GetMessageType(message)          -- type discriminator from native property/header
  |      (empty -> acknowledge + drop, no poison loop)
  2. (messageId, causationId, conversationId) = ExtractContext(message)
  |  MessageContext.StartDispatchFromTransport(messageId, causationId, conversationId)
  |      *** the wire-supplied MessageId/CausationId now flow through to the handler ***
  3. self-published filter (events only):
  |      sourceApp = ExtractMessageHeader(message, ConduitHeaders.SourceApp)
  |      if !IsRequest(message) && ApplicationName is non-empty
  |            && sourceApp == ApplicationName && IsEventType(messageType):
  |          MessageContext.Observe(SelfPublishedEventSkipped(messageType))
  |          AcknowledgeAsync(message); return   -- costs no scope, no deserialize
  4. scope = ServiceProvider.CreateAsyncScope()
  |  wireMediator = scope.ServiceProvider.GetRequiredService<IWireMediator>()
  |  body -> JsonElement payload -> DispatchEnvelope(messageType, payload)
  5. dispatch by kind:
  |     IsRequest(message)              -> DispatchRequestAsync           (command/query w/ reply)
  |     IsFireAndForgetCommand(...)     -> DispatchFireAndForgetCommandAsync (no reply)
  |     else                            -> DispatchEventAsync             (event)
  6. MessageContext.RestoreContext(previous)   -- in finally
```

Step 3 only ever fires for the broker transports: this application's own listeners already ran
inline when it called `PublishAsync`, and the per-app event queue is bound to every discovered
event type including self-published ones, so the broker hands the application its own broadcast
back. It must run after step 2 (`MessageContext.Observe` no-ops without a current context) and
before scope creation (a filtered message shouldn't cost a DI scope or a deserialize). It checks
`IsEventType`, not just `!IsRequest`, because a routed `[FireAndForget]` command also arrives with
`IsRequest == false` and must not be dropped. See
[Transports → Self-published events](transports/index.md#self-published-events) for the full
rationale.

### The three-layer middleware

Inside `wireMediator.HandleWireRequestAsync` / `HandleWireEventAsync`, the generated mediator
calls into `ConduitRuntime.HandleWireRequest` / `HandleWireEvent`, which run the layers in order:

**Layer 1 — `IInboundInterceptor` gate (pre-deserialization).** Receives only the
`string messageType`. Runs *before* the payload is deserialized.

```
foreach (IInboundInterceptor interceptor in interceptors):
    if (!await interceptor.ShouldProcessAsync(messageType, ct))
        return Ok(null)   // discarded silently
```

- `InboxInterceptor` (dedup) **returns false** to discard an already-seen message. `MessageContext.Current`
  is available so it can read the transport-supplied `MessageId`.
- `TypeFilterInterceptor` (access control) **throws** a `ConduitException` to reject a
  disallowed type; the catch block converts it to a `Fail` result. (It rejects by throwing, not
  by returning false.)

**Context handling at this layer (recently fixed):** the transport has already called
`StartDispatchFromTransport`, so `MessageContext.Current` is non-null when `HandleWireRequest`
runs. `ConduitRuntime` therefore **only mints a fresh context when `previousContext is null`**:

```csharp
if (previousContext is null)
{
    MessageContext.StartDispatch();
}
```

This is the fix that lets the wire-supplied `MessageId` / `CausationId` propagate to the handler
instead of being re-minted (overwritten) on every inbound message.

**Layer 2 — `IPipelineBehavior<TRequest, TResult>` (post-deserialization).** After
`createEnvelope()` yields the envelope, each generated switch arm deserializes the payload into the typed
message and records it as the audit payload (`ConduitRuntime.CaptureAuditPayload`; a type-level `[DoNotAudit]`
type and a route-forwarded request capture none). Dispatch then runs the strongly-typed pipeline chain via
`ResolvePipelines` + `ExecuteWithBehaviors` (same mechanism as local dispatch). Resilience,
inbox/outbox, and validation behaviors live here.

A host can run these checks for a request without running its handler:
`ConduitRuntime.PreflightWireRequestAsync` runs the layer 1 gates, the read, and the pipeline's
`IInboundCheckBehavior`s. See [Pipeline Behaviors → Inbound checks](pipeline-behaviors.md#inbound-checks-and-the-wire-preflight).

**Layer 3 — handler.** The resolved `ICommandHandler` / `IQueryHandler` / `IEventListener` runs
at the center of the chain, inside the DI scope created in step 3.

### Reply path (request/response)

`DispatchRequestAsync` (broker base) builds the reply and sends it point-to-point — replies
**bypass the topic/exchange** and go straight to the sender's reply queue:

```
result = await wireMediator.HandleWireRequestAsync(envelope, ct)   -- returns DispatchResult
  |  handler exceptions are converted to Fail results inside ConduitRuntime
replyData = serialize(result)                  -- Ok(data) or Fail(code, message)
await SendReplyAsync(message, replyData)       -- best-effort, point-to-point to reply queue
if (handler failed): TryCaptureErrorAsync(...) -- DLQ capture; caller still got the error reply
```

On the outbound side, `BrokerTransportBase.SendRequestAsync` calls `PublishRequestAsync`, awaits
the reply from the reply queue, deserializes it to `DispatchResult<JsonElement?>`, and unwraps it
via `ConduitRuntime.UnwrapReply` (which throws `InvalidOperationException` if the reply carries an
error).

### Event wire path

`DispatchEventAsync` calls `wireMediator.HandleWireEventAsync`, which routes through
`ConduitRuntime.HandleWireEvent`. Wire-inbound events dispatch to local listeners only (via the
private `PublishLocalAsync`) — they are **never re-broadcast** to transports, preventing echo
loops. `HandleWireEvent` lets `EventListenerException` **propagate** so the transport can
retry / dead-letter; all other exceptions are logged and swallowed.

### Outbound F&F capture (`IOutboundInterceptor`)

When an `IOutboundInterceptor` is registered (the outbox), the generated mediator offers each
capturable envelope to `interceptor.Intercept(envelope)` before any local enqueue or transport
send. `Intercept` returns `bool`: `true` means it captured the envelope in the same transaction as
the business write (relayed later, local-then-transport) and the caller must skip local enqueue
and the transport send; `false` means it declined — no ambient transaction was active — and the
caller proceeds exactly as if no interceptor were registered. `[FireAndForget(Immediate = true)]`
messages skip this check entirely, unconditionally. See
[Outbox → What gets captured](outbox.md#what-gets-captured) for which message kinds are offered
and the full capture-eligibility matrix.

---

## Source map

| Concern | File |
|---|---|
| Route resolution, pipeline resolution/execution, wire entry points | `src/Conduit/Runtime/ConduitRuntime.cs` |
| Correlation context (`StartDispatch`, `StartDispatchFromTransport`) | `src/Conduit/Runtime/MessageContext.cs` |
| Routed-vs-broadcast envelope send | `src/Conduit/Runtime/TransportDispatcher.cs` |
| Broker inbound orchestration + reply path | `src/Conduit/Runtime/BrokerTransportBase.cs` |
| Background F&F execution | `src/Conduit/Runtime/ConduitBackgroundDispatcher.cs` |
| Generated `ConduitMediator` body | `src/Conduit.SourceGen/MediatorEmitter.cs` |
| Generated `ConduitDispatch.Execute` (handler + pipeline wiring, attempt boundary) | `src/Conduit.SourceGen/DispatchEmitter.cs` |
| Attempt loop, scopes, delay, retry counter | `src/Conduit/Runtime/DispatchRetryRunner.cs`, `DispatchAttempt.cs` |

See also: [Core Concepts](core-concepts.md), [Pipeline Behaviors](pipeline-behaviors.md),
[Glossary](glossary.md).
