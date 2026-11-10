# Conduit.Resilience

Polly v8 resilience for the Conduit dispatch pipeline. Wraps command/query/event dispatch in a Polly `ResiliencePipeline` (retry, circuit breaker, timeout, etc.) resolved per request type. Depends on `Microsoft.Extensions.Resilience` (which brings Polly v8) and `Conduit` (core). `netstandard`-free — targets `net10.0`.

## Key types

- `ResilienceBehavior<TRequest, TResult>` — `IPipelineBehavior<TRequest, TResult>` that wraps `next()` in a Polly pipeline resolved from `ResiliencePipelineProvider<string>`. One path: it walks `PipelineKeys` — `"Conduit.Type:" + typeof(TRequest).FullName`, then `"Conduit.Namespace:" + ns` for each enclosing namespace on whole segments from the longest (`A.B.C`, `A.B`, `A`), then `"Conduit.Default"` — and takes the first key with a pipeline, trying the typed `ResiliencePipeline<TResult>` then the non-generic `ResiliencePipeline` under each key (so a non-generic type pipeline beats a typed default and a type pipeline beats a namespace one: `ResilienceBehaviorShould.ResolveTheMostSpecificKeyBeforeThePipelineKind`; it used to try typed under every key first). None → `next()` directly (pass-through). The keys are prefixed because they share the app-wide `ResiliencePipelineRegistry<string>`: walking bare namespace segments picked up a pipeline the host registered for itself under `"Orders"` or `"MyApp"` and wrapped every dispatch in that namespace with it (`.IgnoreAHostPipelineNamedLikeANamespace`). The key list is a `static readonly` per closed type; the provider is asked on every dispatch, never cached statically, because it belongs to one host and tests run several hosts in one process (the registry caches the pipelines it builds). Sets `ResilienceBehavior.DispatchKey` on the `ResilienceContext` it executes with (`AuditRetryObservationsShould.NotEmitRetryAttemptedForAPipelineTheBehaviorDidNotRun`).
- `RetryObservationListener` (internal sealed, a Polly `TelemetryListener`, singleton `Instance`) — emits `RetryAttempted` (via `MessageContext.Observe`) from Polly's `OnRetry` telemetry event: `Attempt` = `AttemptNumber + 1` (Polly's is zero-based; dispatch retry numbers from 1 too), the exception's message and `FullName`, `DelayMs` = `RetryDelay.TotalMilliseconds`. A retried result (`HandleResult`) has no exception: `ErrorMessage` is the fixed `"A result of type {type} was handled as a failure."` and `ExceptionType` `""` — never `Outcome.ToString()`, which for a record result is every property value, and the audit trail captures no result elsewhere nor applies its redaction marks to one (`AuditRetryObservationsShould.KeepARetriedResultsValuesOutOfTheObservation`). Polly reports `OnRetry` only for a retry it will make, so an unhandled failure and the exhausted last attempt are not observed (`ResilienceBehaviorShould.ObserveOnlyRealRetries`). `AddResilience` adds it to the unnamed `TelemetryOptions` (idempotent), which Polly.Extensions applies to every pipeline any registry builds — a handler's HTTP-client resilience included — so it ignores an execution without `DispatchKey` (`AuditRetryObservationsShould.NotEmitRetryAttemptedForAPipelineTheBehaviorDidNotRun`). Telemetry listeners run whatever a `SeverityProvider` says. A behavior built without `AddResilience` (a bare `AddResiliencePipelineRegistry`) observes nothing, and so does a pipeline whose `configure` calls `pipeline.ConfigureTelemetry(...)`: Polly.Extensions 8.4.2 assigns the builder a new `TelemetryListener`, replacing the one carrying this listener, so hosts configure telemetry through `services.Configure<TelemetryOptions>` (documented in `docs/resilience.md`).
- `ResilienceBehavior` (non-generic static) — holds shared constants/keys so callers can reference them without type parameters: `DefaultPipelineKey` (`"Conduit.Default"`, internal), `TypePipelineKey(Type)` / `NamespacePipelineKey(string)` (internal; `ConduitResilienceBuilder` registers through them and tests name typed pipelines with them), the internal `DispatchKey` (`"Conduit.Dispatch"`, the marker above), `PipelineKeys(Type)` (internal), and the public `ResiliencePropertyKey<string?>` keys `MessageIdKey` (`"Conduit.MessageId"`) and `ConversationIdKey` (`"Conduit.ConversationId"`). These are populated into the `ResilienceContext.Properties` before execution so Polly callbacks (`OnRetry`, a circuit breaker's `OnOpened`, etc.) can read the current `MessageContext` correlation IDs.
- `ConduitResilienceBuilder` — fluent builder over `IServiceCollection`. Registers named Polly pipelines via `services.AddResiliencePipeline(key, configure)`:
  - `AddDefaultPipeline(configure)` — registers under `"Conduit.Default"` (catch-all fallback). One pipeline instance, so a circuit breaker in it is shared by every request type and event that falls back to it.
  - `AddPipelineForType<T>(configure)` — registers under `"Conduit.Type:" + typeof(T).FullName` (takes precedence over namespace pipelines and the default).
  - `AddPipelineForNamespace(namespacePrefix, configure)` — registers under `"Conduit.Namespace:" + namespacePrefix`; matched on whole segments by the behavior's key walk, longest registered namespace wins.
  - There is no typed overload: a typed pipeline (`HandleResult`) is registered with `services.AddResiliencePipeline<string, TResult>(key, ...)` under one of the three key forms, which `docs/resilience.md` states.
- `ConduitPredicates` — pre-built Polly `PredicateBuilder<T>` factories that understand Conduit's exception hierarchy. Each call returns a fresh builder (safe to chain `.Handle<T>()` without mutating shared state):
  - `TransientErrors<T>()` — handles `TimeoutException` and a `RemoteDispatchException` whose `Code == ErrorCode.InternalError`; nothing else. Not a remote refusal (`RemoteDispatchException` with any other code: every routed 4xx used to be resent, since it is an `InvalidOperationException`), not a local `ConduitException`, not a bare `InvalidOperationException` (which also leaves out `Conduit.Jobs`' `JobOverlapException` without referencing Jobs). `ConduitPredicatesShould.NotRetryARemoteRefusal` / `.RetryARemoteInternalError` read the exception through the real `ConduitRuntime.UnwrapReply`.
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
- Events go through the behavior too, around all of an event's local listeners at once — but the generated listener chain catches each listener's `HandleAsync` failure and throws the collected `EventListenerException` only after the pipeline returns, so such a failure never reaches Polly (measured: a default-`ShouldHandle` retry runs a throwing listener once). Two things do reach it: a listener whose construction throws (the chain's `GetServices` sits outside the per-listener `try`, so it escapes the chain before any listener runs, and Polly can retry it), and a failure thrown by an inner behavior, whose retry reruns every listener, the ones that succeeded included. The `exceptions` list is declared outside the chain, so it is shared across Polly attempts: the final `EventListenerException` carries every attempt's listener failures, a first-run failure included even when the retry succeeded.
- Application retry is intentionally NOT in the broker transports (one-shot dispatch + dead-letter); this behavior is the supported way to add retry/circuit-breaking, layered on `IPipelineBehavior<,>` + Polly.
- `InternalsVisibleTo` exposes internals to `Conduit.Resilience.Tests`.
