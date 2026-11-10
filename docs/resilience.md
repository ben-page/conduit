# Resilience

- [Overview](#overview)
- [Setup](#setup)
- [Which pipeline a dispatch gets](#which-pipeline-a-dispatch-gets)
- [Typed pipelines](#typed-pipelines)
- [Predicates](#predicates)
- [Where the pipeline runs](#where-the-pipeline-runs)
- [Events](#events)
- [Circuit breakers are shared](#circuit-breakers-are-shared)
- [Message ids in Polly callbacks](#message-ids-in-polly-callbacks)
- [Observing retries](#observing-retries)
- [Resilience and dispatch retry](#resilience-and-dispatch-retry)
- [Limitations](#limitations)

## Overview

`Conduit.NET.Resilience` runs each dispatch through a [Polly v8](https://www.pollydocs.org/) resilience pipeline: retry, circuit breaker, timeout, or any other Polly strategy. It is a [pipeline behavior](pipeline-behaviors.md), `ResilienceBehavior<TRequest, TResult>`, and always the outermost one. You declare pipelines for every message type, for a namespace, or for one type; at dispatch time the behavior picks the most specific one that applies and runs the rest of the dispatch inside it. A dispatch no pipeline applies to passes straight through.

The broker transports never retry a message themselves ([Failed messages](transports/index.md#failed-messages)): retry belongs inside the dispatch, here or in [dispatch retry](dispatch-retry.md).

## Setup

Install `Conduit.NET.Resilience` and call `AddResilience` inside `AddConduit`:

```csharp
using Conduit.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

builder.Services.AddConduit(conduit =>
{
    conduit.AddResilience(resilience =>
    {
        // Every message type without a pipeline of its own
        resilience.AddDefaultPipeline(pipeline => pipeline
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = ConduitPredicates.TransientErrors<object>(),
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
            }));

        // Every message type in MyApp.Billing and its sub-namespaces
        resilience.AddPipelineForNamespace("MyApp.Billing", pipeline => pipeline
            .AddTimeout(TimeSpan.FromSeconds(10)));

        // One message type
        resilience.AddPipelineForType<SyncInventory>(pipeline => pipeline
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = ConduitPredicates.TransientErrors<object>(),
            }));
    });
});
```

`AddResilience(Action<ConduitResilienceBuilder>? configure = null)`:

- puts `ResilienceBehavior<,>` first in the pipeline, wherever the call stands among the `AddPipeline` calls, so a retry reruns every other behavior;
- registers Polly's `ResiliencePipelineRegistry<string>`, so the behavior resolves without `configure`, and every dispatch passes through until a pipeline is registered under one of [Conduit's keys](#which-pipeline-a-dispatch-gets);
- adds the listener that [observes retries](#observing-retries) to Polly's `TelemetryOptions`.

Call it once. Each call inserts the behavior again, so a second call runs every dispatch through its pipeline twice, one inside the other.

The builder registers each pipeline in Polly's registry under a key of Conduit's own:

| Method | Key | Applies to |
|---|---|---|
| `AddDefaultPipeline(Action<ResiliencePipelineBuilder> configure)` | `Conduit.Default` | Every dispatch no type or namespace pipeline covers. |
| `AddPipelineForNamespace(string namespacePrefix, Action<ResiliencePipelineBuilder> configure)` | `Conduit.Namespace:{namespace}` | Every message type in the namespace and beneath it, on whole segments. |
| `AddPipelineForType<T>(Action<ResiliencePipelineBuilder> configure)` | `Conduit.Type:{full type name}` | `T` alone. The name is `typeof(T).FullName`, so a nested type's has a `+`. |

## Which pipeline a dispatch gets

For each dispatch the behavior tries these keys in order and takes the first one with a pipeline:

1. `Conduit.Type:` and the message type's full name.
2. `Conduit.Namespace:` and the type's namespace, then each enclosing namespace on whole segments, longest first: `MyApp.Orders.Commands`, `MyApp.Orders`, `MyApp`. So `"MyApp.Orders"` covers `MyApp.Orders.Commands.CreateOrder`, never `MyApp.OrdersArchive.Purge`.
3. `Conduit.Default`.
4. None: the rest of the dispatch runs unwrapped.

The keys live in the application's `ResiliencePipelineRegistry<string>`, beside every string-keyed Polly pipeline the application registers for itself. Conduit looks up no other key, so a pipeline the application registers for its own use (an HTTP client's, say) never wraps a dispatch, even one named like a namespace. The other way round, a pipeline the application registers itself under one of these keys is used exactly as if the builder had registered it. That is how a [typed pipeline](#typed-pipelines) is added, and a pipeline whose configuration needs services ([Message ids in Polly callbacks](#message-ids-in-polly-callbacks)).

## Typed pipelines

The builder's methods take Polly's non-generic `ResiliencePipelineBuilder`. A strategy that judges a result (Polly's `HandleResult`) needs a typed pipeline, `ResiliencePipeline<TResult>`, and the builder has no overload for one. Register it yourself under one of the three keys with Polly's `AddResiliencePipeline<string, TResult>`, beside `AddResilience`:

```csharp
builder.Services.AddConduit(conduit => conduit.AddResilience());

builder.Services.AddResiliencePipeline<string, ExportStatus>(
    "Conduit.Type:" + typeof(GetExportStatus).FullName,
    pipeline => pipeline.AddRetry(new RetryStrategyOptions<ExportStatus>
    {
        ShouldHandle = ConduitPredicates.TransientErrors<ExportStatus>()
            .HandleResult(status => !status.Ready),
        MaxRetryAttempts = 5,
        Delay = TimeSpan.FromSeconds(1),
    }));
```

`TResult` is the result type the dispatch's pipeline is closed over: the `T` of `ICommand<T>` or `IQuery<T>`, and `object` for a void command or an event. Under each key, a typed pipeline whose `TResult` matches is tried before a non-generic one. The lookup still goes key by key, so a type's own non-generic pipeline beats a typed `Conduit.Default`. A typed pipeline under `Conduit.Default` or a namespace key applies only to dispatches of its result type; every other dispatch resolves as if it were not there.

## Predicates

`ConduitPredicates` builds Polly predicates that know Conduit's exceptions. Each call returns a new `PredicateBuilder<T>`, so chaining `.Handle<TException>()` or `.HandleResult(...)` onto one changes no other.

| Predicate | Handles |
|---|---|
| `TransientErrors<T>()` | A `TimeoutException` (a broker request with no reply within its endpoint's `ResponseTimeout` throws one), and a `RemoteDispatchException` whose `Code` is `ErrorCode.InternalError` (the endpoint a request was routed to failed unexpectedly). Nothing else. |
| `AllErrors<T>()` | Every exception except an `OperationCanceledException`. |

`TransientErrors` leaves out what a rerun would get the same answer to: a remote refusal (a `RemoteDispatchException` with any other code: `NotFound`, `Conflict`, `Unauthorized`, `PreconditionFailed`, ...), a local `ConduitException` (`NotFoundException`, `ConflictException`, ...), and any other `InvalidOperationException`. It also leaves out a transport's own failure to reach the other side, such as the `HttpRequestException` of a refused connection. Add what your transports throw:

```csharp
resilience.AddDefaultPipeline(pipeline => pipeline
    .AddRetry(new RetryStrategyOptions
    {
        ShouldHandle = ConduitPredicates.TransientErrors<object>()
            .Handle<HttpRequestException>(),
    }));
```

`T` is the pipeline's result type. The options the builder's methods take (`RetryStrategyOptions`, `CircuitBreakerStrategyOptions`) are Polly's `object` variants, so they take `TransientErrors<object>()`. A typed pipeline's options take `TransientErrors<TResult>()`, as in [Typed pipelines](#typed-pipelines).

**Validation failures reach the pipeline.** A message's [`Validate()`](core-concepts.md#validate) runs inside every behavior, so its refusal (a 400 `ConduitException`) reaches the resilience behavior like a handler's failure. `AllErrors<T>()`, and Polly's default `ShouldHandle` (every exception but `OperationCanceledException`), retry it and count it toward a circuit breaker. Use `TransientErrors<T>()`, or leave 4xx `ConduitException`s out of your own predicate.

## Where the pipeline runs

The behavior runs wherever a handler runs under the pipeline:

- **A dispatch handled on this host**, in process (`ExecuteAsync`, `FetchAsync`, `PublishAsync`) or arriving over any transport: the pipeline wraps the handler, or an event's listeners.
- **Not a request routed to another endpoint.** The sending host runs no pipeline for it, so a pipeline the sender registers for a routed type does nothing; the receiving host's pipeline wraps the handler there. On the sender, a routed call's failure (a `TimeoutException`, a `RemoteDispatchException`) reaches a pipeline only when the call is made inside a dispatch this host handles: a handler that sends it through its own `IMediator`, whose pipeline then reruns that handler, the routed call included.
- **Not a wire request this host forwards by route** to another endpoint: it runs no pipeline on the forwarding host either.

A retry calls the rest of the dispatch again: every other behavior, the message's `Validate()`, and the handler, resolved again from the **same** DI scope. Scoped services carry over from one Polly attempt to the next, a unit of work and everything it has tracked included. A failure that needs fresh state needs [dispatch retry](dispatch-retry.md), which runs each attempt in a new scope.

## Events

The pipeline wraps an event's dispatch too, chosen by the event's type like any message's, around all of its local listeners at once: one Polly execution per publish, not one per listener. The broadcast to transports happens outside it, so a retry never sends an event again.

A failure thrown by a listener's `HandleAsync` does not reach the pipeline. Every listener runs, each failure is caught and collected, and they are thrown together as an `EventListenerException` after the pipeline has returned. Polly never retries a listener that threw, and a circuit breaker never counts it. Two kinds of failure do reach it:

- **A listener that cannot be built.** The event's listeners are resolved inside the pipeline, before any of them runs, so a listener whose constructor throws (or a dependency the container builds for it) fails the attempt, and Polly can retry it like any other failure.
- **A failure thrown by a behavior inside the resilience behavior.** Polly retries it, which runs every listener again, those that already succeeded included.

Listener failures are collected across attempts: the final `EventListenerException` carries every attempt's, so a listener that failed on the first run is reported even when the retry succeeded.

To retry a listener's own work, make that work a command and send it from the listener: the command's dispatch runs a pipeline of its own.

## Circuit breakers are shared

A pipeline is built once per key and shared by every message that resolves to it, so its stateful strategies (a circuit breaker, a rate limiter) are shared too. The `Conduit.Default` pipeline is one instance for every request type and event without a pipeline of its own: a circuit breaker in it counts all of their failures together, and once one message type's failures open it, every other message type falling back to the default is rejected until it closes. A namespace pipeline's breaker is likewise shared by its namespace. Give a message type its own breaker with `AddPipelineForType<T>` when it should fail on its own.

The state lives in the host's process: each instance of a service has breakers of its own. An open breaker rejects a dispatch with Polly's `BrokenCircuitException`, which reaches the caller like any other exception. Over the wire it is an unexpected failure (`InternalError`), unless one of the host's [translators](error-handling.md#translating-an-unexpected-exception) claims it.

## Message ids in Polly callbacks

Before it runs a pipeline, the behavior copies the dispatch's `MessageContext` ids into Polly's `ResilienceContext`, under two public `ResiliencePropertyKey<string?>` keys:

| Key | Value |
|---|---|
| `ResilienceBehavior.MessageIdKey` | The dispatch's `MessageId`. |
| `ResilienceBehavior.ConversationIdKey` | The dispatch's `ConversationId`. |

A strategy's callbacks (`OnRetry`, a circuit breaker's `OnOpened`, ...) read them from `arguments.Context.Properties`. A callback that logs needs a logger, which the builder's methods cannot hand it. Register that pipeline under Conduit's key yourself, in place of the builder method, with the Polly overload that passes the container:

```csharp
builder.Services.AddResiliencePipeline("Conduit.Default", (pipeline, context) =>
{
    var logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Resilience");
    pipeline.AddRetry(new RetryStrategyOptions
    {
        ShouldHandle = ConduitPredicates.TransientErrors<object>(),
        OnRetry = arguments =>
        {
            arguments.Context.Properties.TryGetValue(ResilienceBehavior.MessageIdKey, out var messageId);
            logger.LogWarning("Retrying message {MessageId} in {Delay}", messageId, arguments.RetryDelay);
            return default;
        },
    });
});
```

## Observing retries

Each retry Polly makes inside a dispatch's pipeline is observed on the dispatch's `MessageContext` as a `RetryAttempted`, so the dispatch's [audit record](audit.md#what-gets-captured) lists its retries:

| Field | Value |
|---|---|
| `Attempt` | The attempt that failed, numbered from 1. |
| `ErrorMessage` | The failure's exception message. |
| `ExceptionType` | The exception's full type name. |
| `DelayMs` | The delay before the retry, in milliseconds. |

- A retry on a result (a typed pipeline's `HandleResult`) has no exception. Its `ErrorMessage` names only the result's type (`A result of type {type} was handled as a failure.`) and its `ExceptionType` is empty. The result's values are never recorded: the audit trail captures no result anywhere else, and its redaction attributes do not apply to one.
- A failure the predicate does not handle, and the last attempt of an exhausted retry, are not retries and are not observed.
- Only a pipeline the behavior ran is observed: a retry in a handler's own HTTP-client resilience is not.
- [Dispatch retry](dispatch-retry.md#observing-retries) emits the same observation, so one record can show both kinds.

The observation comes from Polly's telemetry. `AddResilience` adds a Polly `TelemetryListener` to the container's `TelemetryOptions`, which every pipeline the registry builds reports to. Configure Polly's telemetry the same way, with `services.Configure<TelemetryOptions>(...)`. Do not call `pipeline.ConfigureTelemetry(...)` while configuring a pipeline under one of Conduit's keys: it replaces that pipeline's telemetry listener, and its retries are then silently no longer observed.

Conduit counts no Polly retry in its metrics; Polly reports its own on the `Polly` meter ([Observability → Polly](observability.md#polly)).

## Resilience and dispatch retry

They are two mechanisms with different units. A Polly retry reruns the rest of the dispatch in the same DI scope; [dispatch retry](dispatch-retry.md) reruns the whole dispatch in a new scope when the host's policy says the failure needs one, and the resilience behavior runs inside each of its attempts. How the two compose, and how both sit under the jobs ledger: [Dispatch Retry → Composing with Resilience and Jobs](dispatch-retry.md#composing-with-resilience-and-jobs) and [Background Jobs → Failure handling](jobs.md#failure-handling-two-layers-in-order).

## Limitations

- **An event listener's failure never reaches the pipeline**: no retry, and no circuit breaker counts it. Only a listener that cannot be built, or a behavior's failure, is retried ([Events](#events)).
- **A local `[FireAndForget]` void command is wrapped only up to its enqueue.** Its handler runs later on the background dispatcher, outside every behavior, so its failure never reaches Polly. The same command arriving over the wire runs its handler inline, inside the pipeline.
- **A routed request runs no pipeline on the sending host** ([Where the pipeline runs](#where-the-pipeline-runs)).
- **A retry reuses the scope and repeats the handler's side effects.** Work that leaves the process (a send, a call to another service, an event the handler publishes) is done again by every attempt that reaches it, unless it is idempotent.
- **The builder has no typed overload**: a `HandleResult` pipeline is registered with Polly directly ([Typed pipelines](#typed-pipelines)).

---

Previous: [Pipeline Behaviors](pipeline-behaviors.md) | [Home](../README.md) | Next: [Observability](observability.md)
