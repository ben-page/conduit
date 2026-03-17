# Error Handling

- [Overview](#overview)
- [ErrorCode](#errorcode)
- [Built-in exceptions](#built-in-exceptions)
- [Custom error codes](#custom-error-codes)
- [Error flow](#error-flow)
  - [RESTful endpoints](#restful-endpoints)
  - [Envelope endpoint (wire dispatch)](#envelope-endpoint-wire-dispatch)
  - [Unhandled exceptions](#unhandled-exceptions)
  - [Wire events](#wire-events)
- [ErrorCode equality](#errorcode-equality)

## Overview

Conduit provides a structured error model that works across all transports. Handlers throw typed exceptions. Conduit translates them into Problem Details for REST endpoints, `DispatchResult` error envelopes for wire dispatch, and log entries for fire-and-forget events.

## ErrorCode

`ErrorCode` pairs a machine-readable string with an HTTP status code:

```csharp
public record ErrorCode(string Value, int HttpStatusCode);
```

Built-in codes:

| Code | Value | HTTP Status |
|---|---|---|
| `ErrorCode.NotFound` | `"NotFound"` | 404 |
| `ErrorCode.Unauthorized` | `"Unauthorized"` | 401 |
| `ErrorCode.Conflict` | `"Conflict"` | 409 |
| `ErrorCode.InternalError` | `"InternalError"` | 500 |
| `ErrorCode.BadRequest` | `"BadRequest"` | 400 |

## Built-in exceptions

Each maps to its corresponding `ErrorCode`:

```csharp
throw new NotFoundException("Order 42 was not found");
throw new UnauthorizedException("API key is invalid");
throw new ConflictException("Order was already shipped");
```

All three are sealed subclasses of `ConduitException`:

```csharp
public abstract class ConduitException(ErrorCode code, string message) : Exception(message)
{
    public ErrorCode Code => code;
}
```

## Custom error codes

Define your own `ErrorCode` and exception for domain-specific errors:

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
if (command.Amount > currentBalance)
{
    throw new InsufficientCreditException(
        $"Balance {currentBalance} is less than required {command.Amount}");
}
```

## Error flow

### RESTful endpoints

The handler catches the exception and returns an RFC 9457 Problem Details response. The `HttpStatusCode` from `ErrorCode` is used directly, so custom error codes like `InsufficientCredit` with status 422 work automatically.

```
Handler throws ConduitException
  -> Results.Problem(detail: ex.Message, statusCode: ex.Code.HttpStatusCode, title: ex.Code.Value)
```

### Envelope endpoint (wire dispatch)

`ConduitRuntime.HandleWireRequest` catches the exception and wraps it in a `DispatchResult`:

```
Handler throws ConduitException
  -> DispatchResult.Fail(ex.Code.Value, ex.Message)
```

The HTTP transport's `MapConduitEndpoint` maps the error code string to an HTTP status using a hardcoded switch: `NotFound` to 404, `Unauthorized` to 401, `Conflict` to 409, `InternalError` to 500, everything else to 400.

Custom error codes with non-standard HTTP status fall through to 400 on the envelope endpoint. For accurate status codes on custom errors, use RESTful endpoints.

### Unhandled exceptions

Non-`ConduitException` errors are caught, logged at Error level, and returned as `DispatchResult.Fail("InternalError", "An unexpected error occurred")`. The original message is never leaked to the caller.

### Wire events

Wire events are fire-and-forget. Exceptions are caught and logged. No error is returned to the caller.

## ErrorCode equality

`ErrorCode` equality is based on `Value` only. Two instances with the same `Value` but different `HttpStatusCode` are considered equal:

```csharp
var a = new ErrorCode("InsufficientCredit", 422);
var b = new ErrorCode("InsufficientCredit", 400);

a == b  // true
```

`Value` is the wire identity. It appears in `DispatchError.Code` and travels across service boundaries. `HttpStatusCode` is a local concern used only for HTTP responses.

---

Previous: [Pipeline Behaviors](pipeline-behaviors.md) | [Home](../README.md) | Next: [Security](security.md)
