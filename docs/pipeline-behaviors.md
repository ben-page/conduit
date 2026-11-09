# Pipeline Behaviors

- [Overview](#overview)
- [Defining a behavior](#defining-a-behavior)
- [Registration](#registration)
- [Unified across message types](#unified-across-message-types)
- [Per-type filtering](#per-type-filtering)
- [Inbound checks and the wire preflight](#inbound-checks-and-the-wire-preflight)
- [Behaviors and retried dispatches](#behaviors-and-retried-dispatches)

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

The `Conduit.Resilience` package integrates Polly v8 resilience pipelines with Conduit's dispatch pipeline. Retry, circuit breaker, timeout, and other strategies wrap command/query/event dispatch via `IPipelineBehavior`.

### Setup

Install `Conduit.Resilience` and configure in `AddConduit`:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddResilience(resilience =>
    {
        // Default pipeline for all message types
        resilience.AddDefaultPipeline(pipeline => pipeline
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = ConduitPredicates.TransientErrors(),
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
            }));

        // Per-type override
        resilience.AddPipelineForType<SyncInventory>(pipeline => pipeline
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = ConduitPredicates.TransientErrors(),
            }));
    });
});
```

### Exception Predicates

`ConduitPredicates` provides pre-built predicates that understand Conduit's exception hierarchy:

- **`TransientErrors<T>()`** — `TimeoutException`, `InvalidOperationException`. Does NOT handle `NotFoundException`, `UnauthorizedException`, `ConflictException`, `PreconditionFailedException` (business errors).
- **`AllErrors<T>()`** — everything except `OperationCanceledException`.

Compose with your own: chain additional `.Handle<MyException>()` calls on the returned builder.

### Pipeline Resolution

1. Type-specific pipeline (registered via `AddPipelineForType<T>`)
2. Default pipeline (registered via `AddDefaultPipeline`)
3. Pass-through (no wrapping — `next()` called directly)

The resilience behavior is automatically the outermost pipeline behavior, wrapping logging, validation, and other user behaviors. Because the generated `Validate()` runs innermost, validation failures (400 `ConduitException`s) now reach the resilience behavior: `ConduitPredicates.AllErrors<T>()` or Polly's default `ShouldHandle` would retry them and count them toward a circuit breaker. Use `ConduitPredicates.TransientErrors<T>()`, or exclude 4xx `ConduitException`s in a custom predicate.

### MessageContext Enrichment

`MessageContext.Current` is automatically injected into Polly's `ResilienceContext` during execution. Access via `ResilienceBehavior.MessageIdKey` and `ResilienceBehavior.ConversationIdKey` in retry/circuit-breaker event handlers.

---

Previous: [RabbitMQ](transports/rabbitmq.md) | [Home](../README.md) | Next: [Dispatch Retry](dispatch-retry.md)
