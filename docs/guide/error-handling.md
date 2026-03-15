# Error Handling

## ErrorCode

`ErrorCode` is a record that pairs a machine-readable string with an HTTP status code:

```csharp
public record ErrorCode
{
    public string Value { get; }
    public int HttpStatusCode { get; }

    public ErrorCode(string value, int httpStatusCode);
}
```

Conduit ships five built-in codes:

| Code | Value | HTTP Status |
|---|---|---|
| `ErrorCode.NotFound` | `"NotFound"` | 404 |
| `ErrorCode.Unauthorized` | `"Unauthorized"` | 401 |
| `ErrorCode.Conflict` | `"Conflict"` | 409 |
| `ErrorCode.InternalError` | `"InternalError"` | 500 |
| `ErrorCode.BadRequest` | `"BadRequest"` | 400 |

## Built-in Exceptions

Each built-in exception takes a message string and maps to its corresponding `ErrorCode`:

```csharp
throw new NotFoundException("Order 42 was not found");
throw new UnauthorizedException("API key is invalid");
throw new ConflictException("Order was already shipped");
```

All three are `sealed` subclasses of `ConduitException`:

```csharp
public abstract class ConduitException(ErrorCode code, string message) : Exception(message)
{
    public ErrorCode Code => code;
}

public sealed class NotFoundException(string message)
    : ConduitException(ErrorCode.NotFound, message);

public sealed class UnauthorizedException(string message)
    : ConduitException(ErrorCode.Unauthorized, message);

public sealed class ConflictException(string message)
    : ConduitException(ErrorCode.Conflict, message);
```

## Custom Error Codes

Define your own `ErrorCode` and exception subclass for domain-specific errors:

```csharp
public static class AppErrorCodes
{
    public static readonly ErrorCode InsufficientCredit = new("InsufficientCredit", 422);
}

public sealed class InsufficientCreditException(string message)
    : ConduitException(AppErrorCodes.InsufficientCredit, message);
```

Throw it like any other `ConduitException`:

```csharp
public class DeductCreditHandler : ICommandHandler<DeductCredit, Receipt>
{
    public Task<Receipt> HandleAsync(DeductCredit command, CancellationToken cancellationToken)
    {
        if (command.Amount > currentBalance)
        {
            throw new InsufficientCreditException(
                $"Balance {currentBalance} is less than required {command.Amount}");
        }

        // ...
    }
}
```

## Error Flow

When a handler throws a `ConduitException`, the error propagates differently depending on the endpoint type.

### Envelope Endpoint (wire dispatch)

`ConduitRuntime.HandleWireRequest` catches the exception and returns a `DispatchResult<object>` with a `DispatchError`:

```
Handler throws ConduitException
  -> ConduitRuntime catches it
  -> Returns DispatchResult<object>.Fail(ex.Code.Value, ex.Message)
```

The `DispatchError.Code` is always the string `ErrorCode.Value` -- for example `"NotFound"`, `"InsufficientCredit"`. The HTTP transport's `MapConduitEndpoint` then maps that code string to an HTTP status code for the response:

| Error Code | HTTP Status |
|---|---|
| `"NotFound"` | 404 |
| `"Unauthorized"` | 401 |
| `"Conflict"` | 409 |
| `"InternalError"` | 500 |
| Any other code | 400 |

Unhandled exceptions (non-`ConduitException`) are caught separately, logged at Error level, and returned as `DispatchResult.Fail("InternalError", "An unexpected error occurred")` -- the original exception message is never leaked to the caller.

### RESTful Endpoint (generated REST routes)

`ConduitRestHandlers` catches the exception and returns an RFC 9457 Problem Details response:

```
Handler throws ConduitException
  -> ConduitRestHandlers catches it
  -> Returns Results.Problem(
       detail: ex.Message,
       statusCode: ex.Code.HttpStatusCode,
       title: ex.Code.Value)
```

The `HttpStatusCode` from the `ErrorCode` is used directly as the response status. The `Value` becomes the Problem Details `title`, and the exception message becomes `detail`. Custom error codes like `InsufficientCredit` with status 422 work automatically -- no mapping table needed.

### Wire Events

Wire events are fire-and-forget. `ConduitRuntime.HandleWireEvent` catches all exceptions (including `ConduitException`) and logs them. No error is returned to the caller.

## ErrorCode Equality

`ErrorCode` equality is based on `Value` only. Two `ErrorCode` instances with the same `Value` but different `HttpStatusCode` values are considered equal:

```csharp
var a = new ErrorCode("InsufficientCredit", 422);
var b = new ErrorCode("InsufficientCredit", 400);

a == b           // true -- same Value
a.GetHashCode()  // same as b.GetHashCode()
```

`Value` is the wire identity -- it is the string that appears in `DispatchError.Code` and travels across service boundaries. The `HttpStatusCode` is a local concern used only when translating to HTTP responses.

---

Next: [Security](security.md)
