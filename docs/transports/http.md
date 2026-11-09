# HTTP Transport

- [Overview](#overview)
- [RESTful endpoints](#restful-endpoints)
  - [Generic escape hatch](#generic-escape-hatch)
  - [Available verbs](#available-verbs)
- [Binding strategies](#binding-strategies)
- [Responses](#responses)
- [Envelope dispatch](#envelope-dispatch)
  - [Correlation ids](#correlation-ids)
- [When to use which](#when-to-use-which)
- [Outbound HttpTransport](#outbound-httptransport)
  - [Loading from configuration](#loading-from-configuration)

## Overview

The HTTP transport maps commands and queries to HTTP endpoints. It supports two modes: RESTful routes for external APIs, and an envelope dispatch endpoint for internal service-to-service communication.

## RESTful endpoints

Map each message type to a conventional HTTP route. The source generator emits typed extension methods when your project references `Conduit.Transport.Http`. They are `internal` to the project that generates them, so a module library that references the transport too does not collide with its host's: map the routes in the host, whose generated switch covers the messages of every assembly it references.

```csharp
var app = builder.Build();

app.MapConduitGet<GetTodosQuery>("/api/todos")
    .WithTags("Todos");

app.MapConduitGet<GetTodoByIdQuery>("/api/todos/{id}")
    .WithTags("Todos")
    .ProducesProblem(404);

app.MapConduitPost<CreateTodoCommand>("/api/todos")
    .WithTags("Todos");

app.MapConduitPut<UpdateTodoCommand>("/api/todos/{id}")
    .WithTags("Todos")
    .ProducesProblem(404);

app.MapConduitDelete<DeleteTodoCommand>("/api/todos/{id}")
    .WithTags("Todos");
```

Every method returns `RouteHandlerBuilder`, so you can chain anything ASP.NET minimal APIs support: `RequireAuthorization`, `WithTags`, `ProducesProblem`, `WithOpenApi`, rate limiting, CORS.

### Generic escape hatch

For HTTP methods without a dedicated helper:

```csharp
app.MapConduit<CheckTodoCommand>("OPTIONS", "/api/todos/{id}");
```

The generic method picks the correct binding strategy based on the verb.

### Available verbs

| Method | Extension | Accepts |
|---|---|---|
| GET | `MapConduitGet<T>` | Queries only |
| POST | `MapConduitPost<T>` | Commands and queries |
| PUT | `MapConduitPut<T>` | Commands and queries |
| PATCH | `MapConduitPatch<T>` | Commands and queries |
| DELETE | `MapConduitDelete<T>` | Commands and queries |
| Any | `MapConduit<T>` | Commands and queries |

`MapConduitGet` is restricted to queries because GET requests have no body.

## Binding strategies

The generated code picks the correct binding strategy per verb automatically.

**Queries** always use `[AsParameters]` and bind from route segments and query string. This applies regardless of verb.

**Commands** use two strategies:

- **POST, PUT, PATCH** bind from the JSON request body.
- **DELETE** binds from route segments via `[AsParameters]`, matching query behavior.

```csharp
// Query: route/query string binding
public record GetTodoByIdQuery(Guid Id) : IQuery<TodoDto>;

// Command with body binding (POST/PUT/PATCH)
public record CreateTodoCommand(string Title) : ICommand<Guid>;

// Command with route binding (DELETE)
public record DeleteTodoCommand(Guid Id) : ICommand;
```

## Responses

| Message type | Success response |
|---|---|
| `IQuery<T>` | 200 OK with `T` as JSON |
| `ICommand<T>` | 200 OK with `T` as JSON |
| `ICommand` (void) | 204 No Content |

When a handler throws a `ConduitException`, the endpoint returns an RFC 9457 Problem Details response. The status code comes from the exception's `ErrorCode.HttpStatusCode`:

```csharp
// In your handler:
throw new NotFoundException($"Todo {query.Id} not found");

// Returns:
// HTTP 404
// {
//   "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
//   "title": "NotFound",
//   "status": 404,
//   "detail": "Todo 8f3a... not found"
// }
```

A refusal that names its sentence has `detail` written in the reader's language, and the dispatch endpoint writes the `Message` of its error answer the same way, when the host registers an `IDispatchCultureResolver` and an `IRefusalTextRenderer` ([A refusal in the reader's language](../error-handling.md#a-refusal-in-the-readers-language)).

See [Error Handling](../error-handling.md) for built-in error codes and custom error code support.

## Wire dispatch

For internal consumers that speak the Conduit wire protocol:

```csharp
app.MapConduitEndpoint(); // defaults to /api/conduit
```

This registers two POST endpoints:

- **POST /api/conduit/dispatch** — commands and queries. Returns `DispatchResult` envelope.
- **POST /api/conduit/publish** — events. Returns 200 OK.

The message type is sent in the `X-Conduit-Type` request header. The request body contains the plain serialized message JSON — no envelope wrapper. This matches the wire format used by broker transports (type in transport-native properties, body is the raw message).

You can customize the base path:

```csharp
app.MapConduitEndpoint("/internal/conduit");
// Creates /internal/conduit/dispatch and /internal/conduit/publish
```

`MapConduitEndpoint` returns the route group both endpoints are mapped in, so a convention you chain on it applies to both:

```csharp
app.MapConduitEndpoint().RequireAuthorization();
```

The dispatch endpoint maps error code strings to HTTP status codes using a hardcoded switch (`NotFound` to 404, `Unauthorized` to 401, `Conflict` to 409, `InternalError` to 500, everything else to 400). Custom `ErrorCode` values with non-standard status codes fall through to 400. For accurate HTTP status codes on custom errors, use RESTful endpoints.

### Correlation ids

By default the server mints every dispatch's `MessageContext` ids (`MessageId`, `ConversationId`, `CausationId`): each request is the head of a new conversation, and `X-Conduit-Message-Id`, `X-Conduit-Conversation-Id` and `X-Conduit-Causation-Id` request headers are ignored. These ids are not cosmetic. The [inbox](../inbox.md) deduplicates on the `MessageId`, and the audit trail links records by the other two, so a browser that could set them could suppress another dispatch or graft its own onto someone else's conversation.

An endpoint that only other services reach can opt in, so a conversation carries on across the hop:

```csharp
app.MapConduitEndpoint("/internal/conduit", trustCallerContext: true);
```

A trusting endpoint honors the headers only when `X-Conduit-Message-Id` and `X-Conduit-Conversation-Id` are both present and every supplied id is a plain token: 1 to 64 ASCII letters, digits and hyphens (`MessageContextIds.IsValid`). An absent `X-Conduit-Causation-Id` defaults to the message id. Any other set of headers is ignored as a whole and the server mints fresh ids. Either way the request is answered as usual; a bad id is never an error. The outbound `HttpTransport` sends the three headers whenever all three ids are plain tokens, so the choice is made on the receiving side. When any is not (a broker can deliver a foreign producer's ids), it sends none and the receiver mints.

[RESTful endpoints](#restful-endpoints) and routes opted in with `WithConduitDispatchContext()` never honor caller ids. Server-trusted properties from your `IDispatchContextEnricher`s apply on every edge, trusted or not.

## When to use which

**RESTful endpoints** for external APIs: browser clients, mobile apps, third-party integrations. You get conventional routes, OpenAPI/Swagger, and accurate HTTP status codes for all error types.

**Wire dispatch** for internal service-to-service communication and the TypeScript client over HTTP. The `HttpTransport` class speaks this protocol. The `X-Conduit-Type` header carries the full type discriminator, so services do not need shared route configuration.

## Outbound HttpTransport

For C#-to-C# service dispatch, configure an endpoint with HTTP:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("inventory", endpoint =>
    {
        endpoint.UseHttp(httpClient);
        endpoint.RouteNamespace<Inventory.Commands.ReserveStock>();
    });
});
```

`UseHttp` accepts an `HttpClient` with its `BaseAddress` set to the remote service. Manage the `HttpClient` lifetime via `IHttpClientFactory`. The transport POSTs the plain serialized message to `/api/conduit/dispatch` with the type in the `X-Conduit-Type` header. The path prefix is configurable via the `basePath` parameter; when omitted, it falls back to the endpoint's configured `BasePath` and then to `/api/conduit` (see [Loading from configuration](#loading-from-configuration) below — `BaseAddress` still comes from the `HttpClient` you pass in, since the caller owns that).

`HttpTransport` handles commands and queries only. It does not support event delivery. Use SignalR or a message bus transport for events.

`HttpTransport` sends the current `MessageContext` ids as `X-Conduit-*` headers, when all three are plain tokens. The remote service keeps them only if it maps its endpoint with `trustCallerContext: true` ([Correlation ids](#correlation-ids)); otherwise the conversation starts afresh there.

### Loading from configuration

A second `UseHttp(configure?)` overload takes the base address and path from the endpoint's
configuration section instead of a caller-owned `HttpClient`. It registers its own named
`HttpClient` internally (via `AddHttpClient`), so there is nothing to pass:

```json
{
  "Conduit": {
    "Endpoints": {
      "billing": {
        "BaseAddress": "https://billing.example",
        "BasePath": "/api/conduit"
      }
    }
  }
}
```

```csharp
conduit.AddEndpoint("billing", endpoint => endpoint.UseHttp());
```

| Key | Default | Description |
|---|---|---|
| `BaseAddress` | *(required)* | Base address of the remote Conduit host |
| `BasePath` | `/api/conduit` | Path prefix the remote host maps its dispatch endpoint on |

Configuration binds first; a `configure` callback overrides it. The `UseHttp(httpClient, basePath?)`
overload shown above never reads `BaseAddress` from configuration — the `HttpClient` you pass in
already has one — but it does read the endpoint's configured `BasePath` when you don't pass a
`basePath` argument, so the same `Conduit:Endpoints:{name}:BasePath` key applies to either overload.
See [Loading from IConfiguration](../configuration-reference.md#loading-from-iconfiguration)
for the full precedence rule.

One caveat on the `HttpClient` `UseHttp(configure?)` creates for you: the transport is a keyed
singleton, so it calls `IHttpClientFactory.CreateClient(endpointName)` once at construction and
holds that client for the life of the process. `IHttpClientFactory`'s handler rotation — the thing
that picks up DNS changes without leaking sockets — only applies to clients obtained per use, so a
long-lived host will keep talking to whichever address the remote host resolved to at startup. This
is no worse than passing your own long-lived `HttpClient` to the other overload, but the factory is
not buying you rotation here. If the remote address moves behind a changing DNS record, reach the
named client — it is registered under the endpoint name — and give it a bounded connection lifetime:

```csharp
builder.Services.AddHttpClient("billing")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    });
```

---

Previous: [TypeScript Client](typescript-client.md) | [Home](../../README.md) | Next: [Azure Service Bus](azure-service-bus.md)
