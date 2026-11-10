# Security

- [Overview](#overview)
- [Authentication and authorization](#authentication-and-authorization)
  - [The HTTP endpoints](#the-http-endpoints)
  - [The SignalR hub](#the-signalr-hub)
  - [Per-message authorization](#per-message-authorization)
- [TypeFilterInterceptor](#typefilterinterceptor)
  - [A refused event](#a-refused-event)
- [\[LocalOnly\]](#localonly)
- [Who receives an event](#who-receives-an-event)
- [Gates: IInboundInterceptor](#gates-iinboundinterceptor)
- [What a caller may set](#what-a-caller-may-set)
  - [Correlation ids](#correlation-ids)
  - [Client properties](#client-properties)

## Overview

Conduit does not authenticate anyone. Its HTTP endpoints and its SignalR hub are ordinary ASP.NET Core endpoints, so
ASP.NET Core's authentication and authorization apply to them as to any other. On top of that, Conduit decides four
things:

- **Which message types a caller may send.** Without a type filter, an inbound edge accepts every message the host
  handles. With one, it accepts only the types you list, commands, queries and events alike.
- **Which events leave the process.** `[LocalOnly]` keeps an event away from every transport.
- **Which browsers receive an event.** A SignalR hub pushes only `[ClientVisible]` events by default, to every
  connection, unless an `IEventRecipientResolver` decides.
- **What a caller may set on a dispatch.** The HTTP endpoint and the hub mint the correlation ids and ignore the
  caller's, and the HTTP endpoint reads no client properties, unless you opt in.

## Authentication and authorization

### The HTTP endpoints

`MapConduitEndpoint` returns the route group that holds `{basePath}/dispatch` and `{basePath}/publish`, so a convention
chained on it applies to both:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication().AddCookie();   // or JWT bearer, OpenID Connect, ...
builder.Services.AddAuthorization();
builder.Services.AddConduit();
builder.Services.AddConduitSignalR();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapConduitEndpoint().RequireAuthorization();
app.MapHub<ConduitHub>("/conduit").RequireAuthorization();

app.Run();
```

Any endpoint convention works the same way: a named policy (`RequireAuthorization("staff")`), rate limiting, CORS.

A RESTful endpoint (`MapConduitGet`, `MapConduitPost` and the rest, see [HTTP](transports/http.md#restful-endpoints))
is a minimal-API route of its own, and takes conventions the same way:

```csharp
app.MapConduitPost<CreateTodoCommand>("/api/todos").RequireAuthorization();
```

### The SignalR hub

Map `ConduitHub` itself and put the authorization on the mapping, as above. `ConduitHub` is sealed, so it cannot be
subclassed to add `[Authorize]`, and that is deliberate: the server-mode `SignalRTransport` pushes events through
`IHubContext<ConduitHub>`, which reaches only the connections to `ConduitHub`. To SignalR a subclass would be a
different hub, so its clients would never receive an event.

`RequireAuthorization()` on the hub authorizes the connection, once, when the client connects. A check on each
invocation belongs in a pipeline behavior (next section). A C# service calling an authorized hub supplies its token
through `SignalROptions.ConfigureConnection` (an access token provider or a header), and one calling an authorized HTTP
endpoint through the `HttpClient` it passes to `UseHttp`.

### Per-message authorization

ASP.NET Core decides who may reach the endpoint. Whether a given user may run a given message is a pipeline behavior
that throws a refusal: `ForbiddenException` (403) for a user who may not, `UnauthorizedException` (401) for a caller
who is not signed in. Give the behavior the user through an [`IDispatchContextEnricher`](#client-properties), and
mark it `IInboundCheckBehavior` so `ConduitRuntime.PreflightWireRequestAsync` runs it too. A behavior also runs for an
in-process dispatch, which no endpoint authorized. See
[Pipeline Behaviors → Inbound checks and the wire preflight](pipeline-behaviors.md#inbound-checks-and-the-wire-preflight).

## TypeFilterInterceptor

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddTypeFilter(filter => filter
        .AllowType("MyApp.Commands.CreateNoteCommand")
        .AllowType("MyApp.Queries.GetNotesQuery")
        .AllowType(typeof(NoteViewedEvent).FullName!));
});
```

`AddTypeFilter` registers the type filter (`TypeFilterInterceptor`, an internal [gate](#gates-iinboundinterceptor)
you never name). Without the call there is no filter, and every inbound edge accepts every message type the host
handles or routes. With it:

- **Every inbound wire message is checked**: commands, queries and events, on the HTTP envelope endpoint
  (`/dispatch` and `/publish`), the SignalR hub and every broker transport, before its payload is read.
- **A type that is not listed is refused** with `ForbiddenException` (403). The caller is authenticated and the type
  is simply not one it may send, so this is a permission error, not an authentication failure: a client that maps
  401 to a forced sign-in is not sent to the sign-in page by a type the filter blocks.
- **`AllowType` takes the full type name**, the name the wire carries, matched exactly and case-sensitively. There is
  no generic overload; `typeof(T).FullName!` keeps a name refactor-safe.
- **The list is per host.** Calling `AddTypeFilter` again adds to it. An edge cannot have a list of its own: a host
  that also receives over a broker lists the types it receives there as well, or they are refused.
- **It does not apply to a RESTful endpoint or an in-process dispatch.** A RESTful endpoint maps one type
  explicitly, so the mapping is its allow-list, and it dispatches through `IMediator` in the process.

`ConduitRuntime.PreflightWireRequestAsync` runs the filter with the other gates, so a type it would refuse comes back
as the same `ForbiddenException` refusal.

### A refused event

A refused event is answered to its publisher, as a refused request is:

- **HTTP**: `POST {basePath}/publish` returns the `DispatchResult` envelope with the refusal's status, 403 for the
  filter, as `/dispatch` answers a refused request.
- **SignalR**: the hub's `Publish` invocation fails with a `HubException` carrying the refusal's message, so the
  client's `invoke` rejects.
- **A broker** acknowledges it, since a redelivery would be refused again.

The HTTP and SignalR answers are written in the reader's language
([Error Handling](error-handling.md#a-refusal-in-the-readers-language)), and the refusal is logged at Warning without
a stack trace (EventId 7, `RefusedEvent`).

## [LocalOnly]

```csharp
[LocalOnly]
public record UserPasswordChanged(Guid UserId) : IEvent;
```

An event marked `[LocalOnly]` never leaves the process, and neither does an event deriving from one: the attribute is
inherited. `PublishAsync` still runs its local listeners and sends it to no transport, brokers included. The gate fails
closed: the generated mediator broadcasts only the event types the source generator discovered and did not mark
local-only, so an event type it never saw (a private or file-local type, say) stays in the process too. Use it for
personal data, security events and internal coordination. See
[Transports → \[LocalOnly\]](transports/index.md#localonly) for the broadcast model.

## Who receives an event

An event that may leave the process goes to every server-mode and broker transport the host registered, and each
decides who gets it. A broker delivers it to every service subscribed to its type. A SignalR hub decides which
browsers receive it:

- **By default** the hub pushes a `[ClientVisible]` event to every connection, signed in or not, and any other event
  to none.
- **With an `IEventRecipientResolver` registered**, the resolver decides for every event, `[ClientVisible]` or not:
  `EventRecipients.All`, `None`, `Users(userIds)` or `Groups(groupNames)`.

In a multi-tenant host the default therefore pushes a `[ClientVisible]` event to every tenant's browsers. Scope it with
a resolver, and end it with `None`, so an event added later reaches nobody until you decide:

```csharp
public sealed class TenantRecipients : IEventRecipientResolver
{
    public ValueTask<EventRecipients> ResolveAsync(
        string eventType, object @event, CancellationToken cancellationToken)
    {
        var recipients = @event switch
        {
            InvoiceIssuedEvent issued => EventRecipients.Groups([$"tenant:{issued.TenantId}"]),
            _ => EventRecipients.None,
        };
        return ValueTask.FromResult(recipients);
    }
}

builder.Services.AddSingleton<IEventRecipientResolver, TenantRecipients>();
```

The resolver runs once per event, possibly on the outbox's delivery after the publisher's request has ended, so it
decides from the event alone. See [SignalR → Who receives an event](transports/signalr.md#who-receives-an-event) for
groups, lifetimes and failures.

## Gates: IInboundInterceptor

A gate is the host's own check on an inbound wire message, run before the message's payload is read. It implements
one method:

```csharp
public sealed class KillSwitchGate(IOptionsMonitor<KillSwitchOptions> options) : IInboundInterceptor
{
    public Task<bool> ShouldProcessAsync(string messageType, CancellationToken cancellationToken)
    {
        if (options.CurrentValue.DisabledTypes.Contains(messageType))
        {
            throw new ForbiddenException($"'{messageType}' is switched off.");
        }

        return Task.FromResult(true);
    }
}

builder.Services.AddSingleton<IInboundInterceptor, KillSwitchGate>();
```

- **It sees the type name only**, never the payload. `MessageContext.Current` is set before the gates run on a broker
  transport, and on the HTTP endpoint and the hub when the dispatch carries properties or trusted ids, so a gate can
  read the `MessageId` and the properties there; otherwise it is null in a gate.
- **`true` lets the message through. `false` discards it silently**: the handler does not run, a request is answered
  with an empty success (`DispatchResult.Ok(null)`), and a broker acknowledges the message. The inbox's duplicate check
  is a gate that returns `false`.
- **A thrown `ConduitException` refuses the message**, and the refusal is answered to the caller, for an event as
  for a request ([A refused event](#a-refused-event)). The type filter is a gate that throws.
- **The gates run in registration order, and the first `false` or refusal ends the run**: the gates after it are not
  asked. The type filter takes its place in that order when `AddTypeFilter` is called.
- **They run on every inbound wire edge** (the HTTP envelope endpoint, the hub, every broker transport) and in
  `PreflightWireRequestAsync`, never for an in-process dispatch or a RESTful endpoint.

A gate is resolved from each dispatch's scope, so any lifetime works; register a stateless one as a singleton.

## What a caller may set

### Correlation ids

A dispatch's `MessageContext` ids decide more than log correlation: the [inbox](inbox.md) deduplicates on the
`MessageId`, and the [audit trail](audit.md) links records by `ConversationId` and `CausationId`. A caller that could
set them could suppress another dispatch or graft its own onto someone else's conversation. So the HTTP envelope
endpoint and the SignalR hub ignore the ids a caller sends and mint fresh ones, unless the edge opts in:

```csharp
app.MapConduitEndpoint("/internal/conduit", trustCallerContext: true);

builder.Services.AddConduitSignalR(options => options.TrustCallerContext = true);
```

Opt in only on an edge that other services reach and browsers do not. Even a trusting edge honors a caller's ids only
when the message and conversation ids are both present and every id supplied is a plain token: 1 to 64 ASCII letters,
digits and hyphens (`MessageContextIds.IsValid`). One bad id discards the set, the server mints, and the request is
answered as usual. The forwarding side checks too: the C# `HttpTransport` and `SignalRTransport` send a context only
when all three of its ids are plain tokens, and otherwise send none, so the next hop mints. An id can therefore never
carry CR/LF into a log line or a forwarded header. RESTful endpoints and routes opted in with
`WithConduitDispatchContext()` never honor caller ids. See [HTTP](transports/http.md#correlation-ids) and
[SignalR](transports/signalr.md#correlation-ids).

### Client properties

A client can send properties with a dispatch (a tenant code, say), which seed the dispatch's
`MessageContext.Properties`. The hub always reads its `headers` argument. The HTTP envelope endpoint reads
`X-Conduit-Property-*` headers only when it is mapped to:

```csharp
app.MapConduitEndpoint(readClientProperties: true);
```

Your `IDispatchContextEnricher`s run after the client's properties are read, on every HTTP and SignalR edge (RESTful
endpoints and `WithConduitDispatchContext()` routes included), so a value an enricher writes wins. But a key an
enricher leaves alone keeps whatever the client sent, so **an enricher that owns a key must write it or remove it on
every dispatch, unauthenticated ones included**:

```csharp
public sealed class UserIdEnricher : IDispatchContextEnricher
{
    public void Enrich(ClaimsPrincipal? user, IDictionary<string, object?> properties)
    {
        var userId = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId is null)
        {
            properties.Remove("UserId");
            return;
        }

        properties["UserId"] = userId;
    }
}

builder.Services.AddSingleton<IDispatchContextEnricher, UserIdEnricher>();
```

Property names are case-insensitive, so one `Remove` reaches every spelling a client sent. Treat a property no
enricher owns as the caller's claim. See [HTTP → Client properties](transports/http.md#client-properties) and
[SignalR → Client properties](transports/signalr.md#client-properties).

---

Previous: [Error Handling](error-handling.md) | [Home](../README.md) | Next: [Configuration Reference](configuration-reference.md)
