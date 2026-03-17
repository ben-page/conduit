# HTTP Transport

The HTTP transport maps commands and queries to HTTP endpoints. It supports two modes: RESTful routes for external APIs, and an envelope dispatch endpoint for internal service-to-service communication.

## RESTful endpoints

Map each message type to a conventional HTTP route. The source generator emits typed extension methods when your project references `Conduit.Transport.Http`.

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

See [Error Handling](../error-handling.md) for built-in error codes and custom error code support.

## Envelope dispatch

For internal consumers that speak the Conduit wire protocol:

```csharp
app.MapConduitEndpoint(); // defaults to /api/dispatch
```

This registers two POST endpoints:

- **POST /api/dispatch** accepts a `DispatchEnvelope`, dispatches through `IWireMediator`, and returns a `DispatchResult` envelope.
- **POST /api/dispatch/publish** accepts a `DispatchEnvelope` for event publication.

You can customize the base path:

```csharp
app.MapConduitEndpoint("/internal/conduit");
// Creates /internal/conduit and /internal/conduit/publish
```

The envelope endpoint maps error code strings to HTTP status codes using a hardcoded switch (`NotFound` to 404, `Unauthorized` to 401, `Conflict` to 409, `InternalError` to 500, everything else to 400). Custom `ErrorCode` values with non-standard status codes fall through to 400. For accurate HTTP status codes on custom errors, use RESTful endpoints.

## When to use which

**RESTful endpoints** for external APIs: browser clients, mobile apps, third-party integrations. You get conventional routes, OpenAPI/Swagger, and accurate HTTP status codes for all error types.

**Envelope dispatch** for internal service-to-service communication and the TypeScript client over HTTP. The `HttpTransport` class speaks this protocol. The envelope carries a full type discriminator, so services do not need shared route configuration.

## Outbound HttpTransport

For C#-to-C# service dispatch, configure an endpoint with HTTP:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.AddEndpoint("inventory", endpoint =>
        endpoint.UseHttp(httpClient));

    conduit.Route("Inventory.Commands").To("inventory");
});
```

`UseHttp` accepts an `HttpClient` with its `BaseAddress` set to the remote service. Manage the `HttpClient` lifetime via `IHttpClientFactory`. The transport POSTs `DispatchEnvelope` payloads to `/api/dispatch` (configurable via the `path` parameter).

`HttpTransport` handles commands and queries only. It does not support event delivery. Use SignalR or a message bus transport for events.

---

Previous: [TypeScript Client](typescript-client.md) | [Home](../../README.md) | Next: [Azure Service Bus](azure-service-bus.md)
