# Design notes

> The reasoning behind a few decisions, moved out of the user guides so they stay short. Kept for the record; these
> pages are not part of the documentation site.

- [Task, not ValueTask](#task-not-valuetask)
- [Configuration supplies values, not declarations](#configuration-supplies-values-not-declarations)

## Task, not ValueTask

Every handler, pipeline behavior and `IMediator` method returns `Task` or `Task<T>`. `ValueTask<T>` avoids the `Task<T>`
allocation when a method completes synchronously. Switching the public API and the dispatch internals to it was
considered and declined.

### A dispatch rarely completes synchronously

A local command dispatch runs through the generated mediator and dispatch code, the pipeline behaviors, and the handler:

```
mediator.ExecuteAsync(command)
  > ConduitDispatch.Execute(...)                    // generated
    > ConduitRuntime.ExecuteWithBehaviors(...)      // the pipeline behaviors, then Validate()
      > handler.HandleAsync(command)                // your code: a database, an HTTP call
```

Route resolution, the DI lookups and validation are synchronous work done before the first `await`. `ValueTask<T>`
saves an allocation only when the whole chain finishes without waiting, and a handler that reaches a database or a
network waits. A routed dispatch waits on the transport's network I/O. The saving would apply to handlers that do no
I/O at all, which are the cheap ones already.

### The costs are real

- **The cast the generated code relies on.** `ConduitRuntime.CastTask` hands a `Task<TSource>` back as the
  `Task<TTarget>` a generic interface fallback returns, by a checked reference cast, with no allocation and no wrapping
  task. `ValueTask<T>` is a struct; there is no equivalent.
- **Consumer mistakes.** A `ValueTask<T>` may be awaited only once, must not be stored and awaited later, and must not
  be blocked on with `.Result` before it completes. A `Task` forgives all three.
- **The delegate chain.** Pipeline behaviors pass `Func<Task<TResult>>` delegates down the chain. A
  `Func<ValueTask<TResult>>` chain is easier to misuse, for the same reasons.

### What Conduit does instead

- A gate or a behavior with synchronous logic skips the `async` keyword and returns a completed task or the inner
  `Task` directly, with no state machine. The type filter's gate is one: it checks a set and returns
  `Task.FromResult(true)`, or throws.
- `CastTask<TSource, TTarget>` is a checked reference cast, with no allocation, for the generic interface fallbacks in
  generated code.
- Route resolution, pipeline resolution and validation are synchronous methods with no `Task` at all.

The one `ValueTask` a host implements is `IEventRecipientResolver.ResolveAsync`, which usually answers from the event
alone, synchronously, once per published event.

### Precedent

ASP.NET Core uses `ValueTask` in its I/O hot paths (`PipeReader.ReadAsync` returns one) and keeps `Task` on surfaces
applications implement, such as the `RequestDelegate` middleware is built from. The guidance in Stephen Toub's
"Understanding the Whys, Whats, and Whens of ValueTask" (the .NET Blog, 2018) is the same: default to `Task`, and reach
for `ValueTask` where measurement shows the allocation matters.

## Configuration supplies values, not declarations

`AddConduit(IConfiguration, …)` binds the core settings and each endpoint's transport options. Endpoints, routes,
pipelines and type filters stay in code, by choice:

- **Endpoints and routes.** Declaring an endpoint in configuration would need a registry of transport names across the
  six transport packages, so that a string like `"RabbitMQ"` could resolve to a `Use*` call, and a string-based route
  API to replace `RouteNamespace<T>()` and `RouteType<T>()`. Neither exists, and routing by string would give up the
  compile-time check that the generic overloads give: a route names a type that exists.
- **Pipelines and type filters.** `AddPipeline` takes a `Type`, which a configuration binder cannot produce.
- **Unmatched endpoint sections are ignored, not reported.** A `Conduit:Endpoints:{name}` section that no `AddEndpoint`
  call names is never read, because an endpoint's section is read only when the endpoint is declared. That lets one
  shared `appsettings.json` describe every endpoint across a fleet of hosts, each host picking out the endpoints it
  declares. Failing on an unrecognized section would break that pattern; the cost is that a misspelled endpoint name
  goes unnoticed.
- **The keys follow the code.** A key is named after the property the corresponding fluent call sets, nested as the
  property is, so there is no second vocabulary to learn. Amazon SQS's `Region` and `Credentials` are mapped by hand
  only because the binder cannot construct `RegionEndpoint` or the abstract `AWSCredentials`.
