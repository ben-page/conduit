# Pipeline Behaviors

- [Overview](#overview)
- [Defining a behavior](#defining-a-behavior)
- [Registration](#registration)
- [Unified across message types](#unified-across-message-types)
- [Per-type filtering](#per-type-filtering)

## Overview

Pipeline behaviors wrap dispatch calls with cross-cutting concerns like logging, validation, or timing. They form a chain: each behavior calls `next()` to continue or short-circuits by returning early. First registered is outermost.

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
    conduit.AddPipeline(typeof(ValidationBehavior<,>));
});
```

Order matters. First registered is outermost:

```
LoggingBehavior (enter)
  ValidationBehavior (enter)
    Handler
  ValidationBehavior (exit)
LoggingBehavior (exit)
```

## Unified across message types

The same `IPipelineBehavior<TRequest, TResult>` wraps commands, queries, and events. There is no separate event behavior interface.

For void commands and events, `TResult` is `object` and the pipeline returns `Unit.Value`. Your behavior does not need to care about this. Call `next()` and return the result.

## Per-type filtering

Behaviors are registered globally. To apply logic selectively, filter inside the behavior:

```csharp
public class ValidationBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        if (request is ICommand command)
        {
            command.Validate();
        }

        if (request is IQuery<TResult> query)
        {
            query.Validate();
        }

        return await next();
    }
}
```

Check `typeof(TRequest)`, cast to an interface, look for attributes. If the behavior does not apply, call `next()` and pass through.

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

Trace context propagates through broker message headers (W3C `traceparent`/`tracestate`), so commands dispatched to a remote service via RabbitMQ, Kafka, etc. appear as a single distributed trace in Jaeger/Zipkin.

### Metrics

| Metric | Type | Tags |
|---|---|---|
| `conduit.dispatch.count` | Counter | `conduit.message.type`, `conduit.message.kind`, `conduit.outcome` |
| `conduit.dispatch.duration` | Histogram (ms) | `conduit.message.type`, `conduit.message.kind`, `conduit.outcome` |
| `conduit.transport.send.count` | Counter | `conduit.message.type`, `conduit.transport` |
| `conduit.transport.send.duration` | Histogram (ms) | `conduit.message.type`, `conduit.transport` |
| `conduit.transport.receive.count` | Counter | `conduit.message.type`, `conduit.transport`, `conduit.outcome` |

All instrumentation is zero-cost when no OTel listener is attached.

---

Previous: [RabbitMQ](transports/rabbitmq.md) | [Home](../README.md) | Next: [Error Handling](error-handling.md)
