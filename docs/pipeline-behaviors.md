# Pipeline Behaviors

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

---

Previous: [RabbitMQ](transports/rabbitmq.md) | [Home](../README.md) | Next: [Error Handling](error-handling.md)
