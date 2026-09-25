# Conduit.Resilience

Polly v8 resilience for the Conduit dispatch pipeline. Wraps command/query/event dispatch in a Polly `ResiliencePipeline` (retry, circuit breaker, timeout, etc.) resolved per request type. Depends on `Microsoft.Extensions.Resilience` (which brings Polly v8) and `Conduit` (core). `netstandard`-free — targets `net10.0`.

## Key types

- `ResilienceBehavior<TRequest, TResult>` — `IPipelineBehavior<TRequest, TResult>` that wraps `next()` in a Polly pipeline resolved from `ResiliencePipelineProvider<string>`. Resolution order: (1) typed/non-generic pipeline keyed by `typeof(TRequest).FullName`, then (2) the default key `"Conduit.Default"`; if neither resolves, `next()` is called directly (pass-through). Tries the typed `ResiliencePipeline<TResult>` first, then the non-generic `ResiliencePipeline`. Emits a `RetryAttempted` observation (via `MessageContext.Observe`) on each failed attempt so retries surface in the audit trail.
- `ResilienceBehavior` (non-generic static) — holds shared constants/keys so callers can reference them without type parameters: `DefaultPipelineKey` (`"Conduit.Default"`, internal) and the public `ResiliencePropertyKey<string?>` keys `MessageIdKey` (`"Conduit.MessageId"`) and `ConversationIdKey` (`"Conduit.ConversationId"`). These are populated into the `ResilienceContext.Properties` before execution so Polly callbacks (`OnRetry`, `OnCircuitOpened`, etc.) can read the current `MessageContext` correlation IDs.
- `ConduitResilienceBuilder` — fluent builder over `IServiceCollection`. Registers named Polly pipelines via `services.AddResiliencePipeline(key, configure)`:
  - `AddDefaultPipeline(configure)` — registers under `"Conduit.Default"` (catch-all fallback).
  - `AddPipelineForType<T>(configure)` — registers under `typeof(T).FullName` (takes precedence over the default).
  - `AddPipelineForNamespace(namespacePrefix, configure)` — registers under the namespace prefix string.
- `ConduitPredicates` — pre-built Polly `PredicateBuilder<T>` factories that understand Conduit's exception hierarchy. Each call returns a fresh builder (safe to chain `.Handle<T>()` without mutating shared state):
  - `TransientErrors<T>()` — handles `TimeoutException` and `InvalidOperationException`. Deliberately does NOT handle `ConduitException` subtypes (`NotFoundException`, `UnauthorizedException`, `ConflictException`, `PreconditionFailedException`) — those are business outcomes and must not be retried.
  - `AllErrors<T>()` — handles every exception except `OperationCanceledException`.

## Configuration

`ConduitBuilderExtensions.AddResilience(this ConduitBuilder builder, Action<ConduitResilienceBuilder>? configure = null)`:

```csharp
conduit.AddResilience(resilience =>
{
    resilience.AddDefaultPipeline(pipeline => pipeline
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = ConduitPredicates.TransientErrors<object>(),
            MaxRetryAttempts = 3,
        }));

    resilience.AddPipelineForType<CreateOrder>(pipeline => pipeline
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions()));
});
```

- Always registers the pipeline registry (`AddResiliencePipelineRegistry<string>()`) so `ResilienceBehavior` resolves even with no pipelines configured (pass-through mode). `configure` is optional.
- Inserts `ResilienceBehavior<,>` at position `0` of `ConduitOptions.PipelineTypes` so resilience is the **outermost** behavior — retry/circuit-breaker wraps all user-registered pipeline behaviors. Registered as transient via `AddTransient(typeof(ResilienceBehavior<,>))`.

## Notes

- Pipeline ordering is owned by Conduit (`ConduitOptions.PipelineTypes`), not DI enumeration, consistent with the rest of the pipeline model.
- Application retry is intentionally NOT in the broker transports (one-shot dispatch + dead-letter); this behavior is the supported way to add retry/circuit-breaking, layered on `IPipelineBehavior<,>` + Polly.
- `InternalsVisibleTo` exposes internals to `Conduit.Resilience.Tests`.
