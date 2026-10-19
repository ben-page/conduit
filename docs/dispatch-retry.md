# Dispatch Retry

- [What it does](#what-it-does)
- [Setup](#setup)
- [The policy](#the-policy)
- [Options](#options)
- [What each attempt gets](#what-each-attempt-gets)
- [Nested and independent dispatches](#nested-and-independent-dispatches)
- [Which dispatches are covered](#which-dispatches-are-covered)
- [The retry clock and cancellation](#the-retry-clock-and-cancellation)
- [Observing retries](#observing-retries)
- [Composing with Resilience and Jobs](#composing-with-resilience-and-jobs)
- [Limitations](#limitations)

## What it does

Dispatch retry runs a failed top-level command or query again, each attempt in a **fresh DI scope**, when the host's policy says the failure is worth another try. It is for failures that a rerun on clean state cures and that no handler can fix from the inside: a database deadlock victim is the usual example, because the transaction rolled back and the unit of work that saw it is spent.

Conduit knows nothing about which failures qualify. The host supplies an `IDispatchRetryPolicy`; Conduit supplies the attempt loop, the scopes, the delay, the logs and the counter. Without a policy nothing changes: every dispatch runs once, on the caller's provider.

## Setup

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddDispatchRetry<MyRetryPolicy>(options =>
    {
        options.MaxAttempts = 4;
    });
});
```

`AddDispatchRetry<TPolicy>(Action<DispatchRetryOptions>? configure = null)` registers `TPolicy` as a singleton `IDispatchRetryPolicy`, so it is built from the root provider and takes constructor dependencies only from singleton services. Call it once per host: a second call throws `InvalidOperationException`. The options are validated in the call, so a bad value fails at startup and registers nothing. The attempt loop copies them when it is first resolved (at the host's first dispatch), so set them only in `configure`: a later change to the registered `DispatchRetryOptions` is not seen.

## The policy

```csharp
public interface IDispatchRetryPolicy
{
    bool Covers(Type requestType);
    string? RetryReason(DispatchAttemptFailure failure);
}

public sealed record DispatchAttemptFailure(
    Type RequestType, Exception Exception, int Attempt, int MaxAttempts, IServiceProvider AttemptServices);
```

- **`Covers(requestType)`** says whether a top-level dispatch of that request type runs under the policy. A type it does not cover runs once on the caller's provider, as if no policy were registered.
- **`RetryReason(failure)`** is asked after each failed attempt. A non-null string is the reason to retry (it tags the counter and the log); `null` stops and rethrows the attempt's exception. It is asked for the last attempt too: a reason there means the attempts were exhausted, and is counted as such.
- **`AttemptServices` is the failed attempt's scope, still open.** A policy reads its own signal from it: a flag the attempt's unit of work set, say. The scope is disposed right after the question is answered.
- An attempt that ends in an `OperationCanceledException` while the dispatch's own token is cancelled is never offered to the policy.
- A policy that throws is logged (Warning, EventId 19) and treated as `null`: the attempt's own exception is the one the caller sees.

A host that retries deadlock victims would answer `"deadlock"` for the failures its data layer classifies that way, and `null` for everything else, including refusals such as `ConflictException`.

## Options

```csharp
public sealed class DispatchRetryOptions
{
    public int MaxAttempts { get; set; }                       // default 3
    public IList<DispatchRetryDelay> Delays { get; }           // default (10ms, 100ms), (50ms, 300ms)
    public TimeProvider TimeProvider { get; set; }             // default TimeProvider.System
}

public readonly record struct DispatchRetryDelay(TimeSpan Min, TimeSpan Max);
```

- **`MaxAttempts`** is the most attempts a dispatch gets, the first included, and at least 1. `1` turns retries off while keeping fresh scopes.
- **`Delays`** are ranges the wait is drawn from, uniformly. The wait before attempt *n* + 1 comes from entry `min(n - 1, Count - 1)`, so the last range repeats. With the defaults, the wait before attempt 2 is 10-100 ms and before attempt 3 is 50-300 ms. The list may be empty only when `MaxAttempts` is 1; each range needs `Min` of at least zero and at most `Max`.
- **`TimeProvider`** is the retry clock. See [The retry clock](#the-retry-clock-and-cancellation).

## What each attempt gets

Every attempt of a covered top-level dispatch, **the first included**, runs in a new async scope from the root `IServiceScopeFactory`. The pipeline behaviors, the handler and everything they resolve come from that scope. Making attempt 1 fresh too means code that quietly leaned on the caller's scope fails on every run, not only on a retry.

**Fresh for each attempt**, because it lives in the scope: scoped services, behaviors and handlers, a unit of work and everything it tracks, outbox captures. Nothing is reset by hand, so nothing can be forgotten.

**Kept across attempts**:

- the `MessageContext`: one `MessageId`, one `ConversationId`, so the dispatch has one audit record;
- the request object;
- ambient state that flows with the execution context (`AsyncLocal` values such as an HTTP context accessor).

The scope is disposed **before** the wait. A retry holds nothing of the failed attempt (a pooled connection included) while it sleeps.

**A lazy result outlives its scope.** The attempt's scope is disposed before the result reaches the caller or the wire serializer. A covered dispatch that returns an `IAsyncEnumerable`, an `IQueryable` over a scoped context, or any scoped disposable hands its caller something already disposed, although the same handler works without a policy (it then runs on the caller's scope). Materialize such a result inside the handler.

## Nested and independent dispatches

`DispatchAttempt.Current` is an `AsyncLocal` set for the duration of an attempt. It exposes `Number` (1-based) and `MaxAttempts`. Whether a dispatch gets attempts of its own depends on the provider it was made from:

- **Nested**: the dispatching `IServiceProvider` is the current attempt's own scope (compared by reference). The dispatch runs inline on that scope, with no new scope and no retry of its own. A handler that sends a command through its injected `IMediator` is nested, and so is an event listener's dispatch from the same scope, and an item a batch handler sends through its injected `IWireMediator`. The nested dispatch shares the attempt's transaction, and an exception from it fails the attempt that owns it.
- **Independent**: any other provider, even from inside an attempt. A handler that opens its own scope so that the inner command commits on its own makes an independent dispatch. It gets attempts of its own, sets `DispatchAttempt.Current` for them, and restores the outer attempt afterwards.

Only the outermost dispatch retries. A nested dispatch that fails fails its owner, and the owner's attempt is the unit that reruns.

**An in-process wire call starts its own context.** Code inside an attempt that hands a request to `IWireMediator.HandleWireRequestAsync` (a batch handler, say) must start a context of its own first (`MessageContext.StartDispatch()`, restored afterwards). The wire dispatch binds the current context to the running attempt; without a context of its own it binds the caller's, and the caller's own audit record can then be dropped with an abandoned attempt.

## Which dispatches are covered

The boundary sits in the generated `ConduitDispatch` methods that run a handler under the pipeline: `Execute` (commands, void commands), `Fetch` (queries) and `ExecuteVoidWire` (a `[FireAndForget]` command arriving over the wire). Every path reaches them, so every entry point is covered:

- in-process `IMediator.ExecuteAsync` / `FetchAsync`;
- the wire: the SignalR hub, the HTTP wire endpoint, REST endpoints and the broker transports, which reach the generated wire dispatch;
- a job's command and a command sent from an outbox listener, which are ordinary top-level dispatches.

**Not covered:**

- **Events.** Listeners run through `PublishAsync`, which has no attempt boundary.
- **A local `[FireAndForget]` command.** Its generated `Execute` runs the pipeline around an *enqueue* and returns; the handler runs later on `ConduitBackgroundDispatcher` with no pipeline. There is no handler result to fail on in the caller's flow, so there is nothing to retry. (The same command arriving over the wire runs its handler inline via `ExecuteVoidWire`, and is covered.)
- **A routed dispatch.** Sending a command to a remote endpoint runs no local handler; the receiving host's own boundary covers it.
- **A type the policy's `Covers` rejects.**

## The retry clock and cancellation

The wait before each retry is `Task.Delay(delay, options.TimeProvider, cancellationToken)`. `DispatchRetryOptions.TimeProvider` defaults to `TimeProvider.System` and is **deliberately never** the host's DI `TimeProvider`: that is the business clock, and a test host that fakes it would never see a wait end. Only Conduit's own tests set this option.

The dispatch's `CancellationToken` (a hub's `ConnectionAborted`, a request's `RequestAborted`) cancels the wait, and the `OperationCanceledException` reaches the caller. An attempt that fails with an `OperationCanceledException` while the token is cancelled is kept as the answer and never offered to the policy. Any other exception is offered as usual, even with the token cancelled.

## Observing retries

**Log.** Written under the category `Conduit.Runtime.DispatchRetryRunner`:

| EventId | Name | Level | When |
|---|---|---|---|
| 17 | `DispatchRetrying` | Information | a failed attempt is retried: type, attempt, max, reason, delay in ms |
| 18 | `DispatchRetrySucceeded` | Information | a dispatch succeeded on attempt 2 or later |
| 19 | `DispatchRetryPolicyFailed` | Warning | the policy threw while deciding; carries its exception |

The retry log carries no exception: the failure it answers is the host's to log.

**Counter.** `conduit.dispatch.retries` on `Meter("Conduit")` counts each failed attempt the policy gave a reason for.

| Tag | Value |
|---|---|
| `conduit.message.type` | the request type's full name |
| `conduit.retry.reason` | the string the policy returned |
| `conduit.retry.outcome` | `retried`, or `exhausted` when the reason came on the last attempt |

**Observation.** Each retry emits a `RetryAttempted` observation (attempt number, the failure's message and exception type, the delay in ms) on the dispatch's own `MessageContext`, so the dispatch's one audit record lists its retries. A dispatch whose type is audit-suppressed shows them in the log and the counter only. `RetryAttempted` is the same observation `Conduit.Resilience` emits, so a record can show both kinds.

**Audit.** Dispatches nested in an attempt have records of their own; an abandoned attempt's are dropped. See [Audit → Records and retried attempts](audit.md#records-and-retried-attempts).

## Composing with Resilience and Jobs

**`Conduit.Resilience`** is a pipeline behavior, so it runs *inside* each attempt, against that attempt's scope. A Polly retry replays within one attempt; if it exhausts and the handler still throws, the policy is asked, and a retry then starts a new attempt in a new scope with a fresh Polly pipeline. Use Resilience for failures a rerun in the same scope fixes, and dispatch retry for the ones that need new state.

**`Conduit.Jobs`** opens a scope per job attempt and dispatches the job's command from it. Each command attempt's scope is a sibling of the job's scope, created from the root factory. The ledger's retries (minutes apart, surviving a restart) wrap the whole thing: a command that keeps failing runs up to `DispatchRetryOptions.MaxAttempts` times per ledger attempt, so up to nine executions with both at their defaults.

## Limitations

- The retry is in-process and lasts for the duration of the dispatch. It survives nothing: for work that must outlive a restart use the ledger ([Jobs](jobs.md)) or the [outbox](outbox.md).
- A side effect that leaves the transaction (a send, a call to another service) is repeated by every attempt that reaches it. Retried work should hold its external effects until the attempt is kept, or be idempotent.
- A nested dispatch whose effect leaves the attempt (a local `[FireAndForget]` command or event, an event broadcast to a transport that no outbox captures, a routed command or query) has its audit record dropped with an abandoned attempt, although its effect happened. The audit records only the kept attempt's, so it under-records these dispatches. See [Audit → Records and retried attempts](audit.md#records-and-retried-attempts).
- Kept-attempt work registered with `DispatchAttempt.OnKept` runs on the thread that decides the attempt and should not throw. It runs after the attempt's own `try`, so it is never offered to the policy and never causes a retry. Every queued action runs even when one throws; then that exception (an `AggregateException` when several threw) reaches the dispatch's caller in place of its result, or in place of the attempt's own exception when the kept attempt failed.

---

Previous: [Pipeline Behaviors](pipeline-behaviors.md) | [Home](../README.md) | Next: [Error Handling](error-handling.md)
