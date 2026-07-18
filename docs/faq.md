# FAQ

- [Was this AI-generated?](#was-this-ai-generated)
- [Why does Conduit use Task instead of ValueTask?](#why-does-conduit-use-task-instead-of-valuetask)
  - [The dispatch path is always truly async](#the-dispatch-path-is-always-truly-async)
  - [Interceptors already avoid the overhead](#interceptors-already-avoid-the-overhead)
  - [The costs are real](#the-costs-are-real)
  - [What Conduit does instead](#what-conduit-does-instead)
  - [Ecosystem precedent](#ecosystem-precedent)

## Was this AI-generated?

Conduit was built with significant AI assistance (Claude). The code, tests, and documentation were largely AI-generated.

This does not mean it was "vibe coded." Every feature went through a deliberate design process: requirements were discussed, multiple approaches were evaluated, and a design was approved before implementation began. Architecture, API design, and product direction were human-driven. The AI implemented those decisions.

The codebase enforces quality through strict static analysis (`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`), comprehensive test coverage (MSTest + AwesomeAssertions, no mocks), and test-driven development. Code that doesn't pass the analyzers doesn't compile.

If you're contributing: AI-generated code is held to the same standard as hand-written code.

## Why does Conduit use Task instead of ValueTask?

`ValueTask<T>` avoids a `Task<T>` heap allocation when a method completes synchronously. We evaluated switching the public API and internals to `ValueTask<T>` and decided against it.

### The dispatch path is always truly async

A local command dispatch crosses three async boundaries before reaching the handler:

```
mediator.ExecuteAsync(command)
  > ConduitDispatch.Execute(...)
    > ExecuteWithBehaviors(...)
      > handler.HandleAsync(command)    // your code (DB, HTTP, etc.)
```

The synchronous work (route resolution, DI lookups, validation) happens *within* these methods before their first `await`. It does not cause early return. By the time the method yields, it has real I/O to wait on.

`ValueTask<T>` saves an allocation only when a method completes without awaiting. That does not happen here. Remote dispatch is the same: after a synchronous route lookup, the transport does network I/O.

### Interceptors already avoid the overhead

`TypeFilterInterceptor` is the closest thing to a synchronous hot path. It checks a `HashSet` and either throws or calls `next()`. But it already avoids the `async` keyword and returns the `Task` from `next()` directly, so no state machine or extra `Task` is allocated:

```csharp
public Task<object?> InterceptRequestAsync(
    DispatchEnvelope envelope, Func<Task<object?>> next, CancellationToken cancellationToken)
{
    if (!options.Value.AllowedTypes.Contains(envelope.Type))
        throw new ForbiddenException($"Type '{envelope.Type}' is not allowed.");

    return next();
}
```

Any interceptor or pipeline behavior can use this same pattern when its logic is synchronous.

### The costs are real

Switching to `ValueTask<T>` on public interfaces would mean:

- **Lost optimizations.** `ConduitRuntime.CastTask` uses `Unsafe.As` to reinterpret `Task<TSource>` as `Task<TTarget>` with zero allocation. No equivalent exists for `ValueTask<T>` because it is a struct with a different layout.
- **Harder composition.** `Task.WhenAll` does not accept `ValueTask`. Event broadcasting fans out to multiple transports and would need conversion back to `Task`.
- **Consumer foot-guns.** `ValueTask<T>` can only be awaited once, cannot be stored, and cannot safely block with `.Result`.
- **Delegate chain complexity.** Pipeline behaviors and interceptors use `Func<Task<T>>` delegates throughout. `Func<ValueTask<T>>` is less natural and more error-prone.

### What Conduit does instead

Rather than changing the return type, Conduit avoids unnecessary allocations where it matters:

- Interceptors and behaviors with synchronous logic skip the `async` keyword and return the inner `Task` directly.
- `CastTask<TSource, TTarget>` uses `Unsafe.As` for zero-cost generic interface fallbacks in generated code.
- Route resolution, pipeline resolution, and validation are synchronous methods with no `Task` involved.

### Ecosystem precedent

MediatR uses `Task<T>` everywhere. They considered `ValueTask<T>` and rejected it for the same reasons. ASP.NET Core uses `ValueTask` selectively in internal I/O hot paths (Kestrel read/write) but keeps `Task` for public API surfaces like middleware delegates. The .NET team's guidance: use `ValueTask` when profiling shows the allocation matters, not as a default.

---

Previous: [Configuration Reference](configuration-reference.md) | [Home](../README.md)
