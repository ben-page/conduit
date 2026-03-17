# Security

Conduit provides three layers of security alongside ASP.NET Core's authentication and authorization: type filtering to control which messages external clients can dispatch, `[LocalOnly]` to prevent events from leaving the process, and `IWireInterceptor` for custom wire-level middleware.

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

Any inbound wire dispatch for a type not in the allowed set is rejected with `UnauthorizedException` before the message reaches a handler. The type name matched is the full type name from `DispatchEnvelope.Type`.

`TypeFilterInterceptor` implements `IWireInterceptor`. It checks commands and queries only. Events pass through unfiltered because inbound events are server-initiated broadcasts.

The filter is transport-agnostic. It works the same whether the request arrives over HTTP, SignalR, or a service bus.

## [LocalOnly]

```csharp
[LocalOnly]
public record UserPasswordChanged(Guid UserId) : IEvent;
```

Events marked `[LocalOnly]` never leave the process. The source generator omits the transport broadcast call at compile time. Use this for PII, security events, and internal coordination.

See [Transports](transports/index.md) for the full broadcast model.

## IWireInterceptor

Wire interceptors wrap inbound dispatch calls with custom middleware. They operate on the raw `DispatchEnvelope` before deserialization.

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

Register interceptors as singletons:

```csharp
builder.Services.AddSingleton<IWireInterceptor, AuditInterceptor>();
```

Multiple interceptors chain in reverse registration order. First registered is outermost.

```
AuditInterceptor (enter)
  RateLimitInterceptor (enter)
    Handler
  RateLimitInterceptor (exit)
AuditInterceptor (exit)
```

`TypeFilterInterceptor` is itself an `IWireInterceptor`. When you call `AddTypeFilter(...)`, it registers into the same chain.

## ASP.NET Core auth

Conduit does not replace ASP.NET Core auth. The dispatch endpoints are standard ASP.NET Core endpoints. All auth infrastructure works normally.

```csharp
app.MapConduitEndpoint().RequireAuthorization();
app.MapHub<ConduitHub>("/hub").RequireAuthorization();
```

Or use `[Authorize]` on a hub subclass:

```csharp
[Authorize]
public class SecureConduitHub : ConduitHub;
```

```csharp
app.MapHub<SecureConduitHub>("/hub");
```

Use ASP.NET Core auth to verify identity. Use `TypeFilterInterceptor` to restrict which message types authenticated clients can dispatch.

Next: [Configuration Reference](configuration-reference.md)
