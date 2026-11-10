# Security

- [Overview](#overview)
- [TypeFilterInterceptor](#typefilterinterceptor)
- [\[LocalOnly\]](#localonly)
- [IInboundInterceptor](#iwireinterceptor)
- [ASP.NET Core auth](#aspnet-core-auth)
- [Correlation ids](#correlation-ids)

## Overview

Conduit provides three layers of security alongside ASP.NET Core's authentication and authorization: type filtering to control which messages external clients can dispatch, `[LocalOnly]` to prevent events from leaving the process (and `[ClientVisible]` with an `IEventRecipientResolver` to decide which browsers receive the rest), and `IInboundInterceptor` for custom wire-level middleware.

## TypeFilterInterceptor

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddTypeFilter(filter =>
    {
        filter.AllowType("MyApp.Commands.CreateNoteCommand");
        filter.AllowType("MyApp.Commands.DeleteNoteCommand");
        filter.AllowType("MyApp.Queries.GetNotesQuery");
    });
});
```

Any inbound wire dispatch for a type not in the allowed set is rejected with `ForbiddenException` (403) before the message reaches a handler — the caller is authenticated, the type is simply not client-dispatchable, so this is a permission error, not an authentication failure (a client mapping 401 to a forced re-login must not be bounced by a type-filter block). The type name matched is the full type name from `DispatchEnvelope.Type`.

`TypeFilterInterceptor` implements `IInboundInterceptor`, so it checks every inbound message: commands, queries, and events alike. An event a client may publish must be listed too.

A refused event is answered to the publisher, as a refused request is:

- **HTTP** — `POST {basePath}/publish` returns the `DispatchResult` envelope with the error's status (403 for the filter), exactly as `/dispatch` answers a refused request, its message written in the reader's language.
- **SignalR** — the hub's `Publish` invocation fails with a `HubException` carrying the refusal's message, so the client's `invoke` rejects.

The refusal is logged at Warning without a stack trace (EventId 7, `RefusedEvent`), never at Error.

The filter is transport-agnostic. It works the same whether the message arrives over HTTP, SignalR, or a service bus, so a host that also receives over a broker lists the types it receives there as well. A broker transport acknowledges a refused event rather than redelivering it, since a redelivery would be refused again.

`ConduitRuntime.PreflightWireRequestAsync` runs this filter, like every other registered `IInboundInterceptor`, without running the request's handler: a disallowed type comes back as the same `ForbiddenException` refusal the wire answers. See [Pipeline Behaviors → Inbound checks](pipeline-behaviors.md#inbound-checks-and-the-wire-preflight).

## [LocalOnly]

```csharp
[LocalOnly]
public record UserPasswordChanged(Guid UserId) : IEvent;
```

Events marked `[LocalOnly]` never leave the process, and neither does an event deriving from one: the attribute is inherited. The generated mediator broadcasts only the event types the source generator discovered and did not mark local-only, so the gate fails closed: an event type it never saw (a private or file-local type, say) stays in the process too. Use this for PII, security events, and internal coordination.

`[LocalOnly]` keeps an event away from every transport, brokers included. Reaching browsers is a separate, narrower question, answered by `[ClientVisible]`: a SignalR hub pushes a `[ClientVisible]` event to every connection, signed in or not, and any other event to none. In a multi-tenant host a `[ClientVisible]` event therefore still reaches every tenant's browsers, so scope the events with an `IEventRecipientResolver`, which decides for every event who receives it (users, groups, everyone, nobody) in place of that default. See [SignalR → Who receives an event](transports/signalr.md#who-receives-an-event).

See [Transports](transports/index.md) for the full broadcast model.

## IInboundInterceptor

Wire interceptors wrap inbound dispatch calls with custom middleware. They operate on the raw `DispatchEnvelope` before deserialization.

```csharp
using Conduit.Runtime;
using Conduit.Middleware;
using Microsoft.Extensions.Logging;

public class AuditInterceptor(ILogger<AuditInterceptor> logger) : IInboundInterceptor
{
    public async Task<object?> InterceptRequestAsync(
        DispatchEnvelope envelope,
        Func<Task<object?>> next,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Wire request: {Type}", envelope.Type);
        var result = await next();
        logger.LogInformation("Wire request completed: {Type}", envelope.Type);
        return result;
    }

    public async Task InterceptEventAsync(
        DispatchEnvelope envelope,
        Func<Task> next,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Wire event: {Type}", envelope.Type);
        await next();
    }
}
```

Register interceptors as singletons:

```csharp
builder.Services.AddSingleton<IInboundInterceptor, AuditInterceptor>();
```

Multiple interceptors chain in reverse registration order. First registered is outermost.

```
AuditInterceptor (enter)
  RateLimitInterceptor (enter)
    Handler
  RateLimitInterceptor (exit)
AuditInterceptor (exit)
```

`TypeFilterInterceptor` is itself an `IInboundInterceptor`. When you call `AddTypeFilter(...)`, it registers into the same chain.

## ASP.NET Core auth

Conduit does not replace ASP.NET Core auth. The dispatch endpoints are standard ASP.NET Core endpoints. All auth infrastructure works normally.

```csharp
app.MapConduitEndpoint().RequireAuthorization();
app.MapHub<ConduitHub>("/conduit").RequireAuthorization();
```

Or use `[Authorize]` on a hub subclass:

```csharp
[Authorize]
public class SecureConduitHub : ConduitHub;
```

```csharp
app.MapHub<SecureConduitHub>("/conduit");
```

Use ASP.NET Core auth to verify identity. Use `TypeFilterInterceptor` to restrict which message types authenticated clients can dispatch.

## Correlation ids

A dispatch's `MessageContext` ids decide more than log correlation: the inbox deduplicates on the `MessageId`, and the audit trail links records by `ConversationId` and `CausationId`. So the HTTP wire endpoint and the SignalR hub ignore the ids a caller sends and mint fresh ones, unless the edge opts in: `MapConduitEndpoint(trustCallerContext: true)` or `AddConduitSignalR(options => options.TrustCallerContext = true)`. Opt in only on an edge that other services reach and browsers do not. Even a trusting edge honors a caller's ids only when each one is a plain token (1 to 64 ASCII letters, digits and hyphens, `MessageContextIds.IsValid`); otherwise it mints. The forwarding side checks too: the C# `HttpTransport` and `SignalRTransport` send a context only when all three of its ids are plain tokens, whatever produced them (a broker can deliver a foreign producer's ids), and otherwise send none, so the next hop mints. So an id can never carry CR/LF into a log line or a forwarded header. RESTful endpoints and `WithConduitDispatchContext()` routes never honor them. See [HTTP](transports/http.md#correlation-ids) and [SignalR](transports/signalr.md#correlation-ids).

---

Previous: [Error Handling](error-handling.md) | [Home](../README.md) | Next: [Configuration Reference](configuration-reference.md)
