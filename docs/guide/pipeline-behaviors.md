# Pipeline Behaviors

Pipeline behaviors are middleware that wrap every command, query, and event dispatch. They form a Russian-doll chain: each behavior calls `next()` to invoke the next behavior or the handler itself. First registered = outermost.

## Defining a behavior

```csharp
using Conduit.Middleware;
using Microsoft.Extensions.Logging;

public class LoggingBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>
{
    private readonly ILogger<LoggingBehavior<TRequest, TResult>> logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResult>> logger)
    {
        this.logger = logger;
    }

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

The interface has one method:

```csharp
public interface IPipelineBehavior<TRequest, TResult>
{
    Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken);
}
```

Call `next()` to continue down the pipeline. Skip it to short-circuit (e.g., return a cached result or throw a validation exception before the handler runs).

## Registration

Register behaviors inside `AddConduit` using `AddPipeline` with the open generic type:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddPipeline(typeof(LoggingBehavior<,>));
    conduit.AddPipeline(typeof(ValidationBehavior<,>));
});
```

Order matters. First registered is the outermost wrapper.

## Execution order

With the registration above, a command dispatch flows like this:

```
LoggingBehavior (enter)
  ValidationBehavior (enter)
    Handler
  ValidationBehavior (exit)
LoggingBehavior (exit)
```

The outermost behavior sees the raw request first and the final result last.

## Unified across message types

The same `IPipelineBehavior<TRequest, TResult>` wraps commands, queries, and events. There is no separate event behavior interface.

For void commands, `TResult` is `object` and the pipeline returns `Unit.Value`. For events, the same applies -- `TResult` is `object` returning `Unit.Value`. Your behavior code does not need to care about this; just call `next()` and return the result.

## Per-type filtering

Behaviors are registered globally -- they run for every dispatch. To apply logic selectively, filter inside the behavior itself:

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

Check `typeof(TRequest)`, cast to an interface, look for attributes -- whatever you need. If the behavior does not apply, call `next()` and pass through.

Next: [Error Handling](error-handling.md)
