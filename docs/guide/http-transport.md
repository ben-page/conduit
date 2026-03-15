# HTTP Transport

Conduit.Transport.Http provides two ways to expose your messages over HTTP: RESTful endpoints for external APIs, and envelope dispatch for internal service-to-service communication.

## RESTful endpoints

Map each message type to a conventional HTTP route. The source generator emits typed extension methods when your project references Conduit.Transport.Http.

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

app.MapConduitPatch<PatchTodoCommand>("/api/todos/{id}")
    .WithTags("Todos");

app.MapConduitDelete<DeleteTodoCommand>("/api/todos/{id}")
    .WithTags("Todos");
```

Every method returns `RouteHandlerBuilder`, so you can chain anything ASP.NET minimal APIs support: `RequireAuthorization`, `WithTags`, `ProducesProblem`, `WithOpenApi`, rate limiting, CORS, etc.

### Generic escape hatch

For HTTP methods without a dedicated helper, use `MapConduit<T>` with an explicit verb string:

```csharp
app.MapConduit<CheckTodoCommand>("OPTIONS", "/api/todos/{id}");
```

The generic method picks the correct binding strategy based on the verb (body for POST/PUT/PATCH, route for everything else).

### Available verbs

| Method | Extension | Accepts |
|---|---|---|
| GET | `MapConduitGet<T>` | Queries only |
| POST | `MapConduitPost<T>` | Commands and queries |
| PUT | `MapConduitPut<T>` | Commands and queries |
| PATCH | `MapConduitPatch<T>` | Commands and queries |
| DELETE | `MapConduitDelete<T>` | Commands and queries |
| Any | `MapConduit<T>` | Commands and queries |

`MapConduitGet` is restricted to queries because GET requests have no body. The other verbs accept any command or query type.

## Binding strategies

The generated code picks the correct binding strategy per HTTP verb automatically.

**Queries** always use `[AsParameters]` and bind from route segments and query string parameters. This applies regardless of which verb is used.

**Commands** use two strategies depending on the verb:

- **POST, PUT, PATCH** -- commands bind from the JSON request body.
- **DELETE** -- commands bind from route segments via `[AsParameters]`, matching query behavior. DELETE requests typically identify a resource by route, not body.

You don't choose the strategy -- the generated code selects it based on the verb you map to.

```csharp
// Query -- always route/query string binding
public record GetTodoByIdQuery(Guid Id) : IQuery<TodoDto>;

// Command with body binding (POST/PUT/PATCH)
public record CreateTodoCommand(string Title) : ICommand<Guid>;

// Command with route binding (DELETE)
public record DeleteTodoCommand(Guid Id) : ICommand;
```

## Responses

Return types map to HTTP status codes:

| Message type | Success response |
|---|---|
| `IQuery<T>` | 200 OK with `T` as JSON body |
| `ICommand<T>` | 200 OK with `T` as JSON body |
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

Built-in error codes and their HTTP status mappings:

| ErrorCode | HTTP Status |
|---|---|
| `ErrorCode.NotFound` | 404 |
| `ErrorCode.Unauthorized` | 401 |
| `ErrorCode.Conflict` | 409 |
| `ErrorCode.InternalError` | 500 |
| `ErrorCode.BadRequest` | 400 |

You can define custom error codes with any HTTP status:

```csharp
public static readonly ErrorCode RateLimited = new("RateLimited", 429);
```

Custom error codes work correctly with RESTful endpoints because the status code is read directly from `ErrorCode.HttpStatusCode`.

## Envelope dispatch

For internal consumers that speak the Conduit wire protocol, map the envelope endpoint:

```csharp
app.MapConduitEndpoint(); // defaults to /api/dispatch
```

This registers two POST endpoints:

- **POST /api/dispatch** -- accepts a `DispatchEnvelope`, dispatches through `IWireMediator`, returns a `DispatchResult` envelope. Error responses include an HTTP status code mapped from the error code string.
- **POST /api/dispatch/publish** -- accepts a `DispatchEnvelope` for event publication, returns 200 OK with no data.

You can customize the base path:

```csharp
app.MapConduitEndpoint("/internal/conduit");
// Creates /internal/conduit and /internal/conduit/publish
```

### Envelope error code mapping

The envelope endpoint uses a hardcoded string-based switch to map error codes to HTTP status codes:

| Error code string | HTTP Status |
|---|---|
| `"NotFound"` | 404 |
| `"Unauthorized"` | 401 |
| `"Conflict"` | 409 |
| `"InternalError"` | 500 |
| Everything else | 400 |

Custom `ErrorCode` values with non-standard status codes (e.g., 429) fall through to 400 on the envelope endpoint because the mapping uses the string code, not `ErrorCode.HttpStatusCode`. If you need accurate HTTP status codes for custom error codes, use RESTful endpoints instead.

## When to use which

**RESTful endpoints** for external APIs: browser clients, mobile apps, third-party integrations. You get conventional routes, OpenAPI/Swagger support, and accurate HTTP status codes for all error types.

**Envelope dispatch** for internal service-to-service communication and the TypeScript client via HTTP. The `HttpTransport` class speaks this protocol. The envelope format is stable and carries the full type discriminator, so services don't need to share route configuration.

## Outbound HttpTransport

For C#-to-C# service dispatch, configure an endpoint to use HTTP:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("inventory", endpoint =>
        endpoint.UseHttp(httpClient));

    conduit.Route("Inventory.Commands").To("inventory");
});
```

`UseHttp` accepts an `HttpClient` with its `BaseAddress` set to the remote service. The caller is responsible for managing the `HttpClient` lifetime -- typically via `IHttpClientFactory`. The transport POSTs `DispatchEnvelope` payloads to `/api/dispatch` on the remote service (configurable via the optional `path` parameter).

```csharp
// Custom dispatch path
endpoint.UseHttp(httpClient, path: "/internal/conduit");
```

`HttpTransport` handles commands and queries only. It does not support fire-and-forget event delivery -- use SignalR or a message bus transport for events.

Next: [Cross-Service Routing](cross-service-routing.md)
