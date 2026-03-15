# Security

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

Any inbound wire dispatch (HTTP or SignalR) for a type not in the allowed set is rejected with an `UnauthorizedException` before the message reaches a handler. The type name matched against the filter is the full type name from the `DispatchEnvelope.Type` field -- the same `"Namespace.TypeName"` string used on the wire.

`TypeFilterInterceptor` implements `IWireInterceptor`. It checks commands and queries only. Events pass through unfiltered because inbound events are server-initiated broadcasts, not client requests.

A real-world example from the Notes sample:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddTypeFilter(filter =>
    {
        filter.AllowType("Conduit.Sample.Notes.Commands.CreateNoteCommand");
        filter.AllowType("Conduit.Sample.Notes.Commands.DeleteNoteCommand");
        filter.AllowType("Conduit.Sample.Notes.Queries.GetNotesQuery");
    });
});
builder.Services.AddConduitSignalR();
```

Browser clients connecting through SignalR can dispatch these three types and nothing else. An attempt to dispatch `Conduit.Sample.Notes.Commands.PurgeAllDataCommand` would throw `UnauthorizedException` with the message `"Type 'Conduit.Sample.Notes.Commands.PurgeAllDataCommand' is not allowed by the type filter."`.

The filter is transport-agnostic. It works the same whether the request arrives over HTTP (`MapConduitEndpoint`) or SignalR (`ConduitHub`).

## `[LocalOnly]`

```csharp
using Conduit.Attributes;
using Conduit.Messages;

[LocalOnly]
public record UserPasswordChanged(Guid UserId) : IEvent;
```

Events marked `[LocalOnly]` never leave the process. The source generator omits the transport broadcast call at compile time -- there is no runtime check. Use this for PII events, security events, and internal coordination events.

See [Event Transport](event-transport.md) for the full broadcast model and `[LocalOnly]` details.

## IWireInterceptor

Wire interceptors wrap inbound dispatch calls with custom middleware. They sit between the transport endpoint and the generated mediator, operating on the raw `DispatchEnvelope` before deserialization into the concrete type.

```csharp
using Conduit.Dispatch;
using Conduit.Middleware;
using Microsoft.Extensions.Logging;

public class AuditInterceptor(ILogger<AuditInterceptor> logger) : IWireInterceptor
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

The interface:

```csharp
public interface IWireInterceptor
{
    Task<object?> InterceptRequestAsync(
        DispatchEnvelope envelope,
        Func<Task<object?>> next,
        CancellationToken cancellationToken);

    Task InterceptEventAsync(
        DispatchEnvelope envelope,
        Func<Task> next,
        CancellationToken cancellationToken);
}
```

`InterceptRequestAsync` wraps commands and queries. `InterceptEventAsync` wraps events. Call `next()` to continue the chain, or throw/return early to short-circuit.

Register interceptors as singletons:

```csharp
builder.Services.AddSingleton<IWireInterceptor, AuditInterceptor>();
```

Multiple interceptors chain in reverse registration order. Last registered is innermost (closest to the handler). First registered is outermost (sees the request first).

```
First registered:  AuditInterceptor (enter)
Second registered:   RateLimitInterceptor (enter)
                       Handler
Second registered:   RateLimitInterceptor (exit)
First registered:  AuditInterceptor (exit)
```

Note: `TypeFilterInterceptor` is itself an `IWireInterceptor`. When you call `conduit.AddTypeFilter(...)`, it registers `TypeFilterInterceptor` as a singleton `IWireInterceptor` in DI. You can combine it with your own interceptors -- they all participate in the same chain.

## ASP.NET Core authentication and authorization

Conduit does not replace ASP.NET Core auth. The HTTP dispatch endpoint (`MapConduitEndpoint`) and SignalR hub (`ConduitHub`) are standard ASP.NET Core endpoints. All existing auth infrastructure works normally.

Require authentication on the HTTP dispatch endpoint:

```csharp
app.MapConduitEndpoint().RequireAuthorization();
```

Require authentication on the SignalR hub:

```csharp
app.MapHub<ConduitHub>("/hub").RequireAuthorization();
```

Use `[Authorize]` on a custom hub subclass:

```csharp
using Microsoft.AspNetCore.Authorization;
using Conduit.Transport.SignalR;

[Authorize]
public class SecureConduitHub : ConduitHub;
```

```csharp
app.MapHub<SecureConduitHub>("/hub");
```

Authentication middleware, cookie auth, JWT bearer tokens, policy-based authorization -- all work as they would with any ASP.NET Core endpoint. Conduit sits downstream of the middleware pipeline. By the time a dispatch request reaches `TypeFilterInterceptor` or a handler, the user is already authenticated by ASP.NET Core.

**Layered security**: Use ASP.NET Core auth to verify identity, and `TypeFilterInterceptor` to restrict which message types authenticated clients can dispatch.

Next: [Configuration Reference](configuration-reference.md)
