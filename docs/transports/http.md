# HTTP Transport

- [Overview](#overview)
- [Setup](#setup)
- [Options and configuration keys](#options-and-configuration-keys)
  - [Loading from configuration](#loading-from-configuration)
- [RESTful endpoints](#restful-endpoints)
  - [Generic escape hatch](#generic-escape-hatch)
  - [Available verbs](#available-verbs)
- [Binding strategies](#binding-strategies)
- [Responses](#responses)
- [Wire dispatch](#wire-dispatch)
  - [Correlation ids](#correlation-ids)
  - [Client properties](#client-properties)
- [When to use which](#when-to-use-which)
- [Request/response](#requestresponse)
- [Failure handling](#failure-handling)
- [Wire format](#wire-format)
- [Limitations](#limitations)

## Overview

The HTTP transport maps commands and queries to HTTP endpoints. It supports two modes: RESTful routes for external APIs, and a wire dispatch endpoint for internal service-to-service communication and the TypeScript client. Its outbound side, `HttpTransport`, lets one C# service route commands and queries to another's wire dispatch endpoint.

## Setup

On the host that handles the messages, map the wire dispatch endpoint, RESTful routes, or both:

```csharp
var app = builder.Build();

app.MapConduitEndpoint();                                // POST /api/conduit/dispatch and /api/conduit/publish
app.MapConduitPost<CreateTodoCommand>("/api/todos");     // a RESTful route
```

On a host that routes to it, configure an endpoint with `UseHttp`:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("inventory", endpoint =>
    {
        endpoint.UseHttp(options => options.BaseAddress = new Uri("https://inventory-service"));
        endpoint.RouteNamespace<Inventory.Commands.ReserveStock>();
    });
});
```

## Options and configuration keys

**Inbound.** `MapConduitEndpoint` takes its settings as arguments, in code only:

| Parameter | Default | Description |
|---|---|---|
| `basePath` | `/api/conduit` | The route group both endpoints are mapped in: `{basePath}/dispatch` and `{basePath}/publish`. |
| `trustCallerContext` | `false` | Whether the endpoint honors the correlation ids a caller sends ([Correlation ids](#correlation-ids)). |
| `readClientProperties` | `false` | Whether the endpoint reads `X-Conduit-Property-*` headers into the dispatch's properties ([Client properties](#client-properties)). |

**Outbound.** `UseHttp` has two overloads:

- **`UseHttp(httpClient, basePath?)`** takes an `HttpClient` with its `BaseAddress` set to the remote service. The transport holds that one instance for the life of the host and never disposes it, so pass a long-lived client you own, not one you would otherwise create per use from `IHttpClientFactory` (see [Limitations](#limitations)). `basePath` defaults to the endpoint's configured `BasePath`, then to `/api/conduit`.
- **`UseHttp(configure?)`** binds `HttpOptions` from the endpoint's configuration section, then runs the callback, and registers a named `HttpClient` for the endpoint (via `AddHttpClient`, under the endpoint's name), so there is nothing to pass.

`HttpOptions`:

| Property | Default | Description |
|---|---|---|
| `BaseAddress` | *(required)* | Base address of the remote Conduit host. `UseHttp` throws `InvalidOperationException` when neither configuration nor the callback supplies one. |
| `BasePath` | `/api/conduit` | Path prefix the remote host maps its dispatch endpoint on. |

### Loading from configuration

`UseHttp(configure?)` takes the base address and path from `Conduit:Endpoints:{endpointName}`:

```json
{
  "Conduit": {
    "Endpoints": {
      "inventory": {
        "BaseAddress": "https://inventory-service",
        "BasePath": "/api/conduit"
      }
    }
  }
}
```

```csharp
conduit.AddEndpoint("inventory", endpoint => endpoint.UseHttp());
```

Configuration binds first; a `configure` callback overrides it. The `UseHttp(httpClient, basePath?)`
overload never reads `BaseAddress` from configuration (the `HttpClient` you pass in
already has one), but it does read the endpoint's configured `BasePath` when you don't pass a
`basePath` argument, so the same `Conduit:Endpoints:{name}:BasePath` key applies to either overload.
See [Loading from IConfiguration](../configuration-reference.md#loading-from-iconfiguration)
for the full precedence rule.

## RESTful endpoints

Map each message type to a conventional HTTP route. The source generator emits typed extension methods when your project references the `Conduit.NET.Transport.Http` package. They are `internal` to the project that generates them, so a module library that references the transport too does not collide with its host's: map the routes in the host, whose generated switch covers the messages of every assembly it references.

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

| Request | Response |
|---|---|
| `IQuery<T>` | 200 OK with `T` as JSON; 404 Not Found when the handler returns `null` |
| `ICommand<T>` | 200 OK with `T` as JSON; 204 No Content when the handler returns `null` (the command ran, it has nothing to return) |
| `ICommand` (void) | 204 No Content |
| A body that is neither JSON nor `multipart/form-data` (or has no content type) | 415 Unsupported Media Type |
| A body that does not bind: malformed JSON, a literal `null`, a value of the wrong type, a form field that does not convert (a `Guid`, a number, a `bool`, an enum name), a missing required file | 400 Problem Details, `title` `BadRequest`, the parser's message as `detail` |

A POST, PUT or PATCH command also accepts `multipart/form-data` when it declares one `BinaryData` or `ConduitFile` property: the file binds from the form's file part and every other member from the form field named as the member is in JSON (its `[JsonPropertyName]`, else its camelCase name), in any letter case. A multipart body binds exactly what a JSON body could: a `[JsonIgnore]`d member is never bound. Constructor parameters bind first (a positional record), then each settable property no parameter covered (a class with a parameterless constructor). An enum field is read by member name in any letter case, never by number, as on the JSON wire. A file property declared non-nullable (`ConduitFile`, not `ConduitFile?`) is required: a form without it is a 400, not a `null` for the handler.

Both the wire dispatch endpoint and the REST endpoints read and write JSON with Conduit's wire options (`ConduitRuntime.JsonOptions`), independent of the host's own `Microsoft.AspNetCore.Http.Json.JsonOptions`: an enum is its member name, a closed set its bare value, exactly as the generated TypeScript declares them. Conduit registers nothing into the host's options, so the host's other endpoints keep their own settings.

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

- **POST /api/conduit/dispatch**: commands and queries. Returns `DispatchResult` envelope. A request an inbound gate discards (the [inbox](../inbox.md)'s duplicate) is answered `200` with an empty success (`DispatchResult.Ok(null)`), as if it had run.
- **POST /api/conduit/publish**: events. Returns 200 OK, or, for a refused event (the [type filter](../security.md#typefilterinterceptor), an unknown type, a body the event type cannot read), the `DispatchResult` envelope status-coded as `/dispatch` answers a refusal, and for an event whose dispatch failed outside its listeners, the `InternalError` envelope (see [Failure handling](#failure-handling)).

The message type is sent in the `X-Conduit-Type` request header. The request body contains the plain serialized message JSON, with no envelope wrapper. This matches the wire format used by broker transports (type in transport-native properties, body is the raw message).

You can customize the base path:

```csharp
app.MapConduitEndpoint("/internal/conduit");
// Creates /internal/conduit/dispatch and /internal/conduit/publish
```

`MapConduitEndpoint` returns the route group both endpoints are mapped in, so a convention you chain on it applies to both:

```csharp
app.MapConduitEndpoint().RequireAuthorization();
```

A refusal is answered with the HTTP status its `ErrorCode` declares (`DispatchError.Status`), a custom code's included, as the RESTful endpoints answer it (see [Failure handling](#failure-handling)).

### Correlation ids

By default the server mints every dispatch's `MessageContext` ids (`MessageId`, `ConversationId`, `CausationId`): each request is the head of a new conversation, and `X-Conduit-Message-Id`, `X-Conduit-Conversation-Id` and `X-Conduit-Causation-Id` request headers are ignored. These ids are not cosmetic. The [inbox](../inbox.md) deduplicates on the `MessageId`, and the audit trail links records by the other two, so a browser that could set them could suppress another dispatch or graft its own onto someone else's conversation.

An endpoint that only other services reach can opt in, so a conversation carries on across the hop:

```csharp
app.MapConduitEndpoint("/internal/conduit", trustCallerContext: true);
```

A trusting endpoint honors the headers only when `X-Conduit-Message-Id` and `X-Conduit-Conversation-Id` are both present and every supplied id is a plain token: 1 to 64 ASCII letters, digits and hyphens (`MessageContextIds.IsValid`). An absent `X-Conduit-Causation-Id` defaults to the message id. Any other set of headers is ignored as a whole and the server mints fresh ids. Either way the request is answered as usual; a bad id is never an error. The outbound `HttpTransport` sends the three headers whenever all three ids are plain tokens, so the choice is made on the receiving side. When any is not (a broker can deliver a foreign producer's ids), it sends none and the receiver mints.

[RESTful endpoints](#restful-endpoints) and routes opted in with `WithConduitDispatchContext()` never honor caller ids. Server-trusted properties from your `IDispatchContextEnricher`s apply on every edge, trusted or not.

### Client properties

An endpoint mapped to read them takes each `X-Conduit-Property-<name>` request header into the dispatch's `MessageContext.Properties`, on `/dispatch` and `/publish`, as the SignalR hub reads its `headers` argument: `X-Conduit-Property-TenantCode: t1` is the property `TenantCode` with the value `"t1"`. This is how the [TypeScript client](typescript-client.md#properties)'s `HttpTransport` sends the `headers` you pass it. The endpoint reads none unless you ask: an HTTP edge fails closed.

```csharp
app.MapConduitEndpoint(readClientProperties: true);
```

**An enricher that owns a key must write it or remove it on every dispatch, unauthenticated ones included.** Your `IDispatchContextEnricher`s run after the client's properties are read, so an enricher's write wins, but a dispatch it leaves alone keeps whatever the client sent under that key: an enricher that returns early without a signed-in user lets an anonymous request dispatch with `UserId=victim`. With no identity, remove the keys you own:

```csharp
var sub = user?.FindFirst("sub")?.Value;
if (sub is null) { properties.Remove("UserId"); return; }
```

- **Property names are case-insensitive.** The prefix is matched in any letter case, as header names are, and so is the property: `Properties["TenantCode"]` finds `X-Conduit-Property-TenantCode`, and also the `x-conduit-property-tenantcode` that HTTP/2 and HTTP/3 deliver (they lower-case every header name). An enricher's `properties.Remove("UserId")` removes every spelling a client sent.
- The value is a string. A header sent twice arrives as one comma-joined value.
- A client can set any property no enricher writes or removes: treat one as the caller's claim, as you would the hub's `headers`.
- The correlation-id rule above is unaffected: a property is never a correlation id, even one named `X-Conduit-Message-Id`. A request that carries properties and no trusted ids gets fresh server ids.
- [RESTful endpoints](#restful-endpoints) and routes opted in with `WithConduitDispatchContext()` read no client properties.

## When to use which

**RESTful endpoints** for external APIs: browser clients, mobile apps, third-party integrations. You get conventional routes, OpenAPI/Swagger, and Problem Details responses.

**Wire dispatch** for internal service-to-service communication and the TypeScript client over HTTP. The `HttpTransport` class speaks this protocol. The `X-Conduit-Type` header carries the full type discriminator, so services do not need shared route configuration.

## Request/response

A routed command or query is POSTed by `HttpTransport` as the plain serialized message to `{basePath}/dispatch` on the endpoint's base address, with the type in the `X-Conduit-Type` header. The remote endpoint runs the handler and answers its `DispatchResult`; the transport returns the result's data to the caller. The wait is bounded by the `HttpClient`'s `Timeout` (100 seconds unless you change it), not by a timeout of the transport's own. On the receiving side the dispatch runs under the request's `RequestAborted` token, so a caller that gives up cancels it.

A routed `[FireAndForget]` command is POSTed to `/dispatch` and its reply discarded; the remote endpoint runs the handler before it answers, so the send returns once the handler has run. An event sent through the transport goes to `/publish`. The routes and the endpoint's registration only ever hand `HttpTransport` commands and queries, though: HTTP has no server-to-client push, and an `HttpTransport` endpoint is not one of the transports a publish broadcasts to.

`HttpTransport` sends the current `MessageContext` ids as `X-Conduit-*` headers, when all three are plain tokens. The remote service keeps them only if it maps its endpoint with `trustCallerContext: true` ([Correlation ids](#correlation-ids)); otherwise the conversation starts afresh there.

## Failure handling

**On the server.** Nothing is retried or dead-lettered: the caller gets the answer.

- The wire dispatch endpoint answers a refusal with the `DispatchResult` envelope and the HTTP status its `ErrorCode` declares (`DispatchError.Status`): 404 for a `NotFoundException`, 409 for a `ConflictException`, a custom code's own status. A handler that fails unexpectedly is answered 500, with `InternalError`, the fixed message `An unexpected error occurred.` and a support reference the server logs with the exception.
- `/publish` answers a refused event the same way. An event whose listener throws, or whose dispatch fails outside its listeners (a pipeline behavior throws, a listener cannot be resolved, a gate throws something other than a refusal), is answered as `/dispatch` answers an unexpected failure: 500, `InternalError`, the fixed message and the support reference the server logged the failure under.
- An unexpected failure on either route releases the [inbox](../inbox.md#when-a-dispatch-fails) record its dispatch made, so on an endpoint mapped with `trustCallerContext: true` a caller's retry under the same `MessageId` (the outbox relay's next attempt, a resilience retry) runs instead of being discarded as a duplicate. A refusal stays recorded.
- A RESTful endpoint answers a `ConduitException` with Problem Details ([Responses](#responses)); any other exception propagates to the host's exception handling.

**On the client.** A failure is reported honestly. A refusal from the remote endpoint (a `DispatchResult` error, JSON) throws `RemoteDispatchException` with its code and message, and its support reference for an unexpected failure. Any other non-success answer, a reverse proxy's 502 page or an authentication middleware's bare 401, throws `HttpRequestException` carrying the status. A success that is not JSON (a fallback route's HTML page under a wrong base path) is not a reply either: a request throws `HttpRequestException` rather than return `null`.

## Wire format

| Part | Value |
|---|---|
| Method and path | `POST {basePath}/dispatch` (commands, queries) or `POST {basePath}/publish` (events) |
| `Content-Type` | `application/json` |
| `X-Conduit-Type` | Full type name (e.g. `Orders.Commands.CreateOrder`) |
| Body | The plain serialized message (`ConduitRuntime.JsonOptions`), no envelope |
| `X-Conduit-Message-Id`, `X-Conduit-Conversation-Id`, `X-Conduit-Causation-Id` | `MessageContext` ids, sent by the C# `HttpTransport` when all three are plain tokens; read only by a trusting endpoint |
| `X-Conduit-Property-<name>` | A client property, sent by the TypeScript `HttpTransport`; read only by an endpoint mapped with `readClientProperties: true` |
| Response | `/dispatch`: the `DispatchResult` as JSON, 200 on success, the error's status on a failure. `/publish`: 200 with no body, or the `DispatchResult` of a refusal or an unexpected failure |

## Limitations

- **No server-to-client push.** Events travel only from client to server over HTTP, and a publish never broadcasts to an HTTP endpoint. Use SignalR to push events to browsers, or a broker between services.
- **One `HttpClient` for the life of the host.** Either overload holds its client for good (`UseHttp(configure?)` calls `IHttpClientFactory.CreateClient` once), so the factory's handler rotation, which picks up DNS changes, never happens. If the remote address can move behind DNS, bound the connection lifetime on the handler: `new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) }` for a client you pass in, or on the named client `UseHttp(configure?)` registers under the endpoint's name:

  ```csharp
  builder.Services.AddHttpClient("inventory")
      .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
      {
          PooledConnectionLifetime = TimeSpan.FromMinutes(2),
      });
  ```

- **No retry.** A failed POST is reported, not retried. On the sending host neither [dispatch retry](../dispatch-retry.md) nor [Resilience](../resilience.md) wraps a routed request ([Where the pipeline runs](../resilience.md#where-the-pipeline-runs)): retry at the caller, or make the handler on the other side idempotent so a resend is safe.
- **RESTful endpoints never take caller context.** They ignore caller correlation ids and client properties ([Correlation ids](#correlation-ids)).
- **Files are small and single.** A command takes at most one `BinaryData` or `ConduitFile` property, read whole into memory; a GET or DELETE command, bound from the route, takes none. Streaming and range requests are not supported.

---

Previous: [SignalR](signalr.md) | [Home](../../README.md) | Next: [TypeScript Client](typescript-client.md)
