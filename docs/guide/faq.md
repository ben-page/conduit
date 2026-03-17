# FAQ

## Was this AI-generated?

Conduit was built with significant AI assistance (Claude). The code, tests, and documentation were largely AI-generated.

This does not mean it was "vibe coded." Every feature went through a deliberate design process: requirements were discussed, multiple approaches were evaluated with trade-offs, and a design was approved before implementation began. Architecture, API design, and product direction were human-driven. The AI implemented those decisions.

The codebase enforces quality through strict static analysis (`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`), comprehensive test coverage (MSTest + AwesomeAssertions, no mocks), and test-driven development. Code that doesn't pass the analyzers doesn't compile.

If you're contributing: AI-generated code is held to the same standard as hand-written code. If it doesn't meet the project's conventions and standards, it will not be accepted.

## Why does Conduit use Task instead of ValueTask?

`ValueTask<T>` avoids a `Task<T>` heap allocation when a method completes synchronously. We evaluated switching the public API and internals to `ValueTask<T>` and decided against it. Here's why.

### The dispatch path is always truly async

A local command dispatch crosses three async boundaries before reaching the handler:

```
mediator.ExecuteAsync(command)          // async — state machine
  → ConduitDispatch.Execute(...)        // async — resolves handler
    → ExecuteWithBehaviors(...)         // async — chains pipeline
      → handler.HandleAsync(command)    // async — your code (DB, HTTP, etc.)
```

The synchronous work — route resolution, DI lookups, validation — happens *within* these methods before their first `await`. It doesn't cause early return. By the time the method yields, it has real I/O to wait on. `ValueTask<T>` saves an allocation only when a method completes without awaiting, and that doesn't happen here.

Remote dispatch is the same story: after a synchronous route lookup, the transport does network I/O.

### Interceptors already avoid the overhead

`TypeFilterInterceptor` is the closest thing to a synchronous hot path — it checks a `HashSet` and either throws or calls `next()`. But it already avoids the `async` keyword and returns the `Task` from `next()` directly, so no state machine or extra `Task` is allocated:

```csharp
public Task<object?> InterceptRequestAsync(
    DispatchEnvelope envelope, Func<Task<object?>> next, CancellationToken cancellationToken)
{
    if (!options.Value.AllowedTypes.Contains(envelope.Type))
        throw new UnauthorizedException($"Type '{envelope.Type}' is not allowed.");

    return next();
}
```

Any interceptor or pipeline behavior can use this same pattern when its logic is synchronous. `ValueTask` isn't needed.

### The costs are real

Switching to `ValueTask<T>` on public interfaces would mean:

- **Breaking change** for every handler, pipeline behavior, interceptor, and transport implementation.
- **Lost optimizations** — `ConduitRuntime.CastTask` uses `Unsafe.As` to reinterpret `Task<TSource>` as `Task<TTarget>` with zero allocation. No equivalent exists for `ValueTask<T>` (it's a struct with different layout).
- **Harder composition** — `Task.WhenAll` doesn't accept `ValueTask`. Event broadcasting fans out to multiple transports and would need conversion back to `Task` anyway.
- **Consumer foot-guns** — `ValueTask<T>` can only be awaited once, can't be stored, and can't safely block with `.Result`. Library consumers must know these rules.
- **Delegate chain complexity** — Pipeline behaviors and interceptors use `Func<Task<T>>` delegates throughout the chaining. `Func<ValueTask<T>>` is less natural and more error-prone.

### What Conduit does instead

Rather than changing the return type, Conduit avoids unnecessary allocations where it matters:

- Interceptors and behaviors with synchronous logic skip the `async` keyword and return the inner `Task` directly (no state machine).
- `CastTask<TSource, TTarget>` uses `Unsafe.As` for zero-cost generic interface fallbacks in generated code.
- Route resolution, pipeline resolution, and validation are all synchronous methods — no `Task` involved.

### Ecosystem precedent

MediatR uses `Task<T>` everywhere and considered `ValueTask<T>` — they rejected it for the same reasons. ASP.NET Core uses `ValueTask` selectively in internal I/O hot paths (Kestrel read/write) but keeps `Task` for public API surfaces like middleware delegates. The .NET team's guidance: use `ValueTask` when profiling shows the allocation matters, not as a default.
