# Pipeline Behaviors

- [Overview](#overview)
- [Defining a behavior](#defining-a-behavior)
- [Registration](#registration)
- [Unified across message types](#unified-across-message-types)
- [Per-type filtering](#per-type-filtering)
- [Inbound checks and the wire preflight](#inbound-checks-and-the-wire-preflight)
- [Behaviors and retried dispatches](#behaviors-and-retried-dispatches)
- [OpenTelemetry](#opentelemetry)
- [Resilience (Polly)](#resilience-polly)

## Overview

Pipeline behaviors wrap dispatch calls with cross-cutting concerns like logging, authorization, or timing. They form a chain: each behavior calls `next()` to continue or short-circuits by returning early. First registered is outermost.

## Defining a behavior

```csharp
using Conduit.Middleware;
using Microsoft.Extensions.Logging;

public class LoggingBehavior<TRequest, TResult>(
    ILogger<LoggingBehavior<TRequest, TResult>> logger)
    : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Handling {RequestType}", typeof(TRequest).Name);
        var result = await next();
        logger.LogInformation("Handled {RequestType}", typeof(TRequest).Name);
        return result;
    }
}
```

Call `next()` to continue down the pipeline. Skip it to short-circuit.

## Registration

Register behaviors inside `AddConduit` with the open generic type:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddPipeline(typeof(LoggingBehavior<,>));
    conduit.AddPipeline(typeof(AuthorizationBehavior<,>));
});
```

Order matters. First registered is outermost:

```
LoggingBehavior (enter)
  AuthorizationBehavior (enter)
    Validate()
    Handler
  AuthorizationBehavior (exit)
LoggingBehavior (exit)
```

## Unified across message types

The same `IPipelineBehavior<TRequest, TResult>` wraps commands, queries, and events. There is no separate event behavior interface.

For void commands and events, `TResult` is `object` and the pipeline returns `Unit.Value`. Your behavior does not need to care about this. Call `next()` and return the result.

## Per-type filtering

Behaviors are registered globally. To apply logic selectively, filter inside the behavior:

```csharp
public class AuthorizationBehavior<TRequest, TResult>(ICurrentUser user) : IPipelineBehavior<TRequest, TResult>
{
    public Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        if (request is ICommand && !user.CanWrite)
        {
            throw new ForbiddenException("Read-only users cannot run commands.");
        }

        return next();
    }
}
```

Check `typeof(TRequest)`, cast to an interface, look for attributes. If the behavior does not apply, call `next()` and pass through.

A behavior does not call the message's `Validate()`. The generated dispatch calls it innermost — after every behavior, immediately before the handler — so a behavior that refuses (like the one above) never runs the caller's input, and every message is still validated before its handler runs.

## Inbound checks and the wire preflight

A host sometimes needs to know whether the wire would admit a request without running it: a batch endpoint that judges each item before it starts any, say. `ConduitRuntime.PreflightWireRequestAsync(contractName, payload, services, cancellationToken)` answers that. It returns a `WirePreflightResult(bool Admitted, DispatchError? Refusal)`:

| Result | Meaning |
|---|---|
| `(true, null)` | admitted: every gate passed, and either the payload read as the contract and every inbound check ran through, or the contract is routed to another endpoint, which is admitted unread and unchecked (step 2 below) |
| `(false, error)` | refused: `error` is what the wire would answer, a `ConduitException`'s code and data, a translator's refusal (`IWireExceptionTranslator`), or an unexpected failure as `InternalError` with its reference |
| `(false, null)` | an `IInboundInterceptor` gate discarded the request, which the wire answers with no error and no data |

Nothing escapes as an exception.

**Marking a check.** A pipeline behavior takes part in the preflight only if it also implements the marker `Conduit.Middleware.IInboundCheckBehavior`:

```csharp
public class AuthorizationBehavior<TRequest, TResult>(ICurrentUser user)
    : IPipelineBehavior<TRequest, TResult>, IInboundCheckBehavior
{
    public Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        // Judges only: calls next() or throws a refusal; writes nothing the request depends on.
        return next();
    }
}
```

The marker is a promise that the behavior only judges whether the request may run. An authorization check qualifies. A transaction, an audit, or a behavior that shapes the request does not, and must not carry it. The preflight reads the marker from the registered pipeline types, so a check added to the pipeline anywhere counts without further wiring.

**What the preflight runs**, in order:

1. The host's `IInboundInterceptor` gates, through the same gate loop the wire uses (`TypeFilterInterceptor` is one).
2. If the contract is routed to another endpoint: stop and admit. The wire forwards a routed request unread, so its checks belong to the receiving host.
3. The read: the contract is found by name among the registry's commands and queries and read from the payload with `DeserializeWire`, exactly as the wire reads it.
4. Every pipeline behavior that is an `IInboundCheckBehavior`, in the pipeline's order, closed over the contract's own result type as dispatch closes them, around a step that does nothing.

**What it never runs:** the handler, any pipeline behavior that is not an inbound check, and the contract's own `Validate()` (dispatch runs it innermost, after every behavior). A name the registry does not list as a command or query is answered as the wire answers an unknown type: a `BadRequest` refusal ([Error Handling](error-handling.md#unhandled-exceptions)).

**It records nothing.** The gates run under no `MessageContext`, as for a wire request no transport identified, so an inbox gate records nothing. The checks run in a fresh context with no observer attached, so nothing is audited. The caller's context is current again when it returns. It starts no transaction and no retry attempt.

## Behaviors and retried dispatches

When the host registers a [dispatch retry policy](dispatch-retry.md), a behavior runs once **per attempt**, and every behavior and the handler of an attempt resolve from that attempt's own scope. A behavior that logs or times a dispatch sees each attempt separately; `DispatchAttempt.Current` (its `Number` and `MaxAttempts`) says which one. A behavior is never shared across attempts through the scope, only through singletons or `AsyncLocal` state.

## OpenTelemetry

Conduit has built-in distributed tracing and metrics. Spans are created automatically for dispatch, wire dispatch, and transport send/receive. No pipeline behavior needed.

### Setup

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Conduit"))
    .WithMetrics(m => m.AddMeter("Conduit"));
```

### Traces

Conduit creates spans for:
- Local command/query/event dispatch (`ConduitMediator`)
- Wire request/event dispatch (`ConduitRuntime`)
- Transport send and receive (`BrokerTransportBase`)

Tags: `conduit.message.type`, `conduit.message.kind`, `conduit.message.id`, `conduit.conversation.id`, `conduit.dispatch` (local/remote), `conduit.endpoint`, `conduit.transport`.

Trace context propagates through broker message headers (W3C `traceparent`/`tracestate`), so commands dispatched to a remote service via RabbitMQ, etc. appear as a single distributed trace in Jaeger/Zipkin.

### Metrics

| Metric | Type | Tags |
|---|---|---|
| `conduit.dispatch.count` | Counter | `conduit.message.type`, `conduit.message.kind`, `conduit.outcome` |
| `conduit.dispatch.duration` | Histogram (ms) | `conduit.message.type`, `conduit.message.kind`, `conduit.outcome` |
| `conduit.transport.send.count` | Counter | `conduit.message.type`, `conduit.transport` |
| `conduit.transport.send.duration` | Histogram (ms) | `conduit.message.type`, `conduit.transport` |
| `conduit.transport.receive.count` | Counter | `conduit.message.type`, `conduit.transport`, `conduit.outcome` |
| `conduit.dispatch.retries` | Counter | `conduit.message.type`, `conduit.retry.reason`, `conduit.retry.outcome` |

`conduit.dispatch.count` and `conduit.dispatch.duration` measure every dispatch the mediator runs once: in-process `ExecuteAsync`/`FetchAsync`/`PublishAsync`, a wire request or event arriving over any transport, and an outbox's delivery to this host's listeners. `conduit.message.kind` is `command`, `query` or `event` (a wire request takes its type's declared kind). A wire request or event naming a type the host does not declare -- one forwarded by route, or one nothing handles -- is tagged `conduit.message.type` = `(undeclared)` (kind `request` for a request), because the caller chooses that name and must not be able to mint metric series. `conduit.outcome` is `error` when the dispatch threw, `success` otherwise. A dispatch from inside a handler is measured as its own dispatch, and a request routed to another service is measured on both hosts.

All instrumentation is zero-cost when no OTel listener is attached.

## Resilience (Polly)

The `Conduit.NET.Resilience` package integrates Polly v8 resilience pipelines with Conduit's dispatch pipeline. Retry, circuit breaker, timeout, and other strategies wrap command/query/event dispatch via `IPipelineBehavior`.

### Setup

Install the `Conduit.NET.Resilience` package and configure in `AddConduit`:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddResilience(resilience =>
    {
        // Default pipeline for all message types
        resilience.AddDefaultPipeline(pipeline => pipeline
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = ConduitPredicates.TransientErrors<object>(),
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
            }));

        // Per-namespace override: every message type in MyApp.Billing and its sub-namespaces
        resilience.AddPipelineForNamespace("MyApp.Billing", pipeline => pipeline
            .AddTimeout(TimeSpan.FromSeconds(10)));

        // Per-type override
        resilience.AddPipelineForType<SyncInventory>(pipeline => pipeline
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = ConduitPredicates.TransientErrors<object>(),
            }));
    });
});
```

### Exception Predicates

`ConduitPredicates` provides pre-built predicates that understand Conduit's exception hierarchy:

- **`TransientErrors<T>()`** — `TimeoutException` (a broker request that gets no reply within its `ResponseTimeout` throws one), and a `RemoteDispatchException` whose `Code` is `ErrorCode.InternalError` (the endpoint a request was routed to failed unexpectedly). Does NOT handle a remote refusal (a `RemoteDispatchException` with any other code: `NotFound`, `Conflict`, `Unauthorized`, `PreconditionFailed`, ...), a local `ConduitException` (`NotFoundException`, `UnauthorizedException`, `ConflictException`, `PreconditionFailedException`, ...), or any other `InvalidOperationException`: resending those gets the same answer.
- **`AllErrors<T>()`** — everything except `OperationCanceledException`.

`T` is the pipeline's result type. The non-generic `RetryStrategyOptions` and `CircuitBreakerStrategyOptions` that `AddDefaultPipeline`, `AddPipelineForNamespace` and `AddPipelineForType<T>` take are the `object` variants, so pass `TransientErrors<object>()`.

Compose with your own: chain additional `.Handle<MyException>()` calls on the returned builder.

### Pipeline Resolution

1. Type-specific pipeline (registered via `AddPipelineForType<T>`)
2. Namespace pipeline (registered via `AddPipelineForNamespace`): the type's namespace, else the longest registered namespace enclosing it, matched on whole segments (`"MyApp.Orders"` covers `MyApp.Orders.Commands.CreateOrder`, never `MyApp.OrdersArchive.Purge`)
3. Default pipeline (registered via `AddDefaultPipeline`)
4. Pass-through (no wrapping — `next()` called directly)

Conduit registers these pipelines in the application's `ResiliencePipelineRegistry<string>` under keys of its own: `Conduit.Type:{type full name}`, `Conduit.Namespace:{namespace}` and `Conduit.Default`. It looks up no other key, so a pipeline the application registers there for its own use (an HTTP client's, say) never wraps a dispatch, whatever it is named. Under each key, a typed pipeline (`ResiliencePipeline<TResult>`, which retrying on a result with `HandleResult` needs) registered with `services.AddResiliencePipeline<string, TResult>(key, ...)` is preferred to a non-generic one. Resolution goes key by key, so a type's own non-generic pipeline still beats a typed default.

A pipeline is built once per key and shared by every message that resolves to it, so a stateful strategy is shared too. The `Conduit.Default` pipeline is one instance for every request type and event without a pipeline of its own: a circuit breaker in it counts all of their failures together, and when one message type's failures open it, every other message type falling back to the default is rejected until it closes. A namespace pipeline's breaker is likewise shared by its namespace. Give a message type its own breaker with `AddPipelineForType<T>` when it should fail on its own.

The pipeline wraps an event's dispatch as well, around all of its local listeners at once. A failure thrown by a listener's `HandleAsync` does not reach it: every listener the container built runs, and their failures are thrown together as an `EventListenerException` after the pipeline has returned, so Polly never retries a listener that threw. Two kinds of failure do reach it. A listener whose construction throws (its constructor, or a dependency the container builds for it) fails while the event's listeners are resolved, before they run, and Polly can retry that like any other failure. And a failure thrown by a behavior inside the resilience behavior is retried, which runs every listener again, including those that already succeeded. Listener failures are collected across attempts: the final `EventListenerException` carries every attempt's, so a listener that failed on the first run is reported even when it succeeded on the retry.

The resilience behavior is automatically the outermost pipeline behavior, wrapping logging, validation, and other user behaviors. Because the generated `Validate()` runs innermost, validation failures (400 `ConduitException`s) reach the resilience behavior: `ConduitPredicates.AllErrors<T>()` or Polly's default `ShouldHandle` would retry them and count them toward a circuit breaker. Use `ConduitPredicates.TransientErrors<T>()`, or exclude 4xx `ConduitException`s in a custom predicate.

### MessageContext Enrichment

`MessageContext.Current` is automatically injected into Polly's `ResilienceContext` during execution. Access via `ResilienceBehavior.MessageIdKey` and `ResilienceBehavior.ConversationIdKey` in retry/circuit-breaker event handlers.

Each retry Polly makes is observed on the dispatch's `MessageContext` as a `RetryAttempted` (the attempt that failed, numbered from 1, its exception's message and full type name, and the delay before the retry in ms), so the dispatch's audit record lists its retries. A retry on a result (a typed pipeline's `HandleResult`) names only the result's type, never its values: the audit trail captures no result anywhere else, and its redaction attributes do not apply to one. A failure the predicate does not handle, and the last attempt of an exhausted retry, are not retries and are not observed.

The observation comes from Polly's retry telemetry, which `AddResilience` hooks through the container's `TelemetryOptions`. Configure Polly's telemetry the same way, with `services.Configure<TelemetryOptions>(...)`. Do not call `pipeline.ConfigureTelemetry(...)` inside `AddDefaultPipeline`, `AddPipelineForNamespace` or `AddPipelineForType<T>`: that replaces the pipeline's telemetry listener, and the pipeline's retries are then silently no longer observed.

---

Previous: [TypeScript Client](transports/typescript-client.md) | [Home](../README.md) | Next: [Dispatch Retry](dispatch-retry.md)
