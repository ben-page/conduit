# Error Handling

- [Overview](#overview)
- [ErrorCode](#errorcode)
- [Built-in exceptions](#built-in-exceptions)
- [Custom error codes](#custom-error-codes)
- [Client-typed refusals](#client-typed-refusals)
- [Error flow](#error-flow)
  - [RESTful endpoints](#restful-endpoints)
  - [Envelope endpoint (wire dispatch)](#envelope-endpoint-wire-dispatch)
  - [Unhandled exceptions](#unhandled-exceptions)
  - [Translating an unexpected exception](#translating-an-unexpected-exception)
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
| `ErrorCode.PreconditionFailed` | `"PreconditionFailed"` | 412 |
| `ErrorCode.InternalError` | `"InternalError"` | 500 |
| `ErrorCode.BadRequest` | `"BadRequest"` | 400 |
| `ErrorCode.ContractMismatch` | `"ContractMismatch"` | 400 |

## Built-in exceptions

Each maps to its corresponding `ErrorCode`:

```csharp
throw new NotFoundException("Order 42 was not found");
throw new UnauthorizedException("API key is invalid");
throw new ConflictException("Order was already shipped");
throw new PreconditionFailedException("Order was modified by someone else. Refresh and try again.");
```

`ConflictException` is for the resource's current state refusing the request — a refresh would show the
same refusal. `PreconditionFailedException` is for a stale version: the caller's ETag no longer matches,
and a refresh gives them a live one to retry with.

All three are sealed subclasses of `ConduitException`:

```csharp
public abstract class ConduitException(ErrorCode code, string message) : Exception(message)
{
    public ErrorCode Code => code;
}
```

## ContractMismatch: a request missing a member

An inbound wire request that is missing a primary-constructor parameter is refused with `ContractMismatchException`
(code `"ContractMismatch"`, status 400). It means the sender was built against an older contract -- typically an
out-of-date browser tab -- and would otherwise silently default (clear) a field it does not know about. The check is
always on; there is no opt-in flag.

- `Message`: `"This page is out of date: the request is missing {names}. Reload to continue."` (names joined `, `). At
  most 20 members are named; the rest are counted (`… and 55 more.`), so a large payload cannot make a large
  error. It declares no data property, so it stays a plain (not client-typed) refusal: `Type`/`Data` are null.
- `Details`: one `DispatchErrorDetail(Field: <wire path>, Message: "missing")` per NAMED member: an object's own missing
  members in constructor order, then those of its nested models in payload order. A top-level member is named by its
  wire name (`eTag`); a member of a nested positional model by its path (`lead.wipLimit`, `columns[1].wipLimit`). A
  dictionary value is named by the contract path `byKey[*].wipLimit`, never by its key: the key is client text, and it
  never enters the message or `Details`. Paths are distinct. They come from the type's bound constructor, its
  properties' types (a collection's element type, a dictionary's value type) and the naming policy, walked against the
  payload -- never from System.Text.Json's exception text. The reflection is cached per type.
- **Explicit `null` is accepted** for a nullable member; only an *absent* key is refused.
- **A parameter with a C# default may be omitted.** That is the declared opt-out: a default is how a member becomes
  optional on the wire.
- **The bound.** The check covers the constructor parameters System.Text.Json BINDS, at any depth: the request type's
  own, and those of every nested model in a property or inside a list or dictionary. A class binds its
  `[JsonConstructor]`, else its single public parameterized constructor when it has no public parameterless one. A
  **struct** binds only a `[JsonConstructor]`: without one STJ builds it through its default constructor and setters,
  so a positional `record struct` is NOT checked -- an absent member silently keeps its default, and the naming does not
  claim otherwise. Nor is a settable or `init` property of a non-positional (parameterless-constructor) class, at any
  depth. Nor does it name members beneath a polymorphic, abstract, interface, `object` or `JsonElement`
  member (its declared type does not say which constructor binds); a fault there is a plain `BadRequest` naming the
  JSON path. A consumer that needs a nested model protected makes it a positional record CLASS (or gives a struct a
  `[JsonConstructor]`).
- **Which reads are strict.** Only `ConduitRuntime.DeserializeWire`, which every generated wire dispatch uses: the
  envelope endpoint, the SignalR hub, batch items, and broker-delivered commands, including a dead letter replayed
  from the DLQ. A command queued on a broker (or dead-lettered) before a deploy added a member is therefore refused
  after it, so **a broker-routed command's new member needs a C# default**. The RESTful endpoints
  (`ConduitRestHandlers`' `ReadFromJsonAsync`) and the multipart binder read with ASP.NET's options and stay lenient:
  a REST request missing a member still defaults it. The shared `ConduitRuntime.JsonOptions` stays lenient because it
  also reads stored `Conduit.Jobs` payloads, remote replies and events: a job stored before a member was added must
  keep running.
- A request with any other fault (wrong-typed field, integer enum ordinal) stays a `BadRequest`, even if a member is also missing.

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

## Client-typed refusals

A `ConduitException` subclass reaches the client as a **typed refusal** — named on the wire (`DispatchError.Type`)
and in TypeGen's generated `ErrorMap`, so client code can narrow on it with `isError` — when
`ConduitException.IsClientTyped(Type)` is true for it. That's any non-abstract subclass declared **outside** the
`Conduit.Contracts` assembly (a consumer's own refusal, such as `InsufficientCreditException` above), or any
subclass anywhere that declares at least one public instance property. Conduit's own built-in status exceptions
(`NotFoundException`, `ConflictException`, etc.) are declared in `Conduit.Contracts` and declare no data, so they
are untyped — a plain `ConflictException` is a category a client checks by `Code`, not a refusal a screen narrows
on by type. One consequence: a consumer's own category exception — sealed and thrown directly, the same shape as
Conduit's built-ins — is declared outside `Conduit.Contracts`, so it is client-typed too and gets an `ErrorMap`
entry; that's harmless (the change is additive — no client branches on a type being *absent* from `ErrorMap`), but
a consumer that wants a category kept out of `ErrorMap` must declare it inside a non-exported type or behind an
abstract base that is never thrown directly.

A typed refusal's data (`DispatchError.Data`) is **never `null`** on the wire, even for a refusal that declares no
properties (or whose `GetClientData` override returns `null`): it serializes as an empty object `{}`. The generated
TypeScript mirrors this — a data-less consumer refusal still gets an `ErrorMap` entry and an `export interface`,
just an empty one (`export interface SomeRefusal {}`), so `isError` narrows it the same way as a refusal that
carries fields.

**Only exported types reach `ErrorMap`.** TypeGen's `ErrorTypeScanner` walks each scanned assembly's
`GetExportedTypes()`. A `private` or nested-`private`/`internal` `ConduitException` subclass can still be
`IsClientTyped` — it's typed on the wire, with its `Type` and `Data` sent exactly as for any other typed refusal —
but it never gets an `ErrorMap` entry or an interface, because TypeGen never sees it. A client can still read its
`type` string and `data` off the envelope, but cannot narrow on it by name with `isError`, and has no generated
interface for its shape. Keep a refusal a client is meant to narrow on public and exported.

## Error flow

### RESTful endpoints

The handler catches the exception and returns an RFC 9457 Problem Details response. `ConduitRestHandlers.ToProblem`
builds it from the exception's status and message **only**:

```
Handler throws ConduitException
  -> Results.Problem(detail: ex.Message, statusCode: ex.Code.HttpStatusCode, title: ex.Code.Value)
```

The `HttpStatusCode` from `ErrorCode` is used directly, so custom error codes like `InsufficientCredit` with status
422 work automatically. But `ToProblem` never reads `ex.Details`, `ex.GetClientType()`, or `ex.GetClientData()` — a
typed refusal's `Type` and `Data` (see [Client-typed refusals](#client-typed-refusals) above), and any structured
`Details`, do **not** reach a RESTful caller. A screen that needs to narrow on a refusal's type or read its data
must go through the envelope endpoint below (or DMS's own global exception-handler path, which sends the status
alone for the same reason).

### Envelope endpoint (wire dispatch)

`ConduitRuntime.HandleWireRequest` catches the exception and wraps it in a `DispatchResult`, which carries the
refusal's status, and — for a client-typed refusal — its type name and data too:

```
Handler throws ConduitException
  -> DispatchResult.Fail(ex)
     -> DispatchError { Code, Message, Details, Status, Type, Data }
```

`Type` and `Data` are set only when `ConduitException.IsClientTyped` is true for the exception's runtime type;
otherwise both are `null`. When `Type` is set, `Data` is never `null` on the wire — a data-less typed refusal still
sends `{}` (see [Client-typed refusals](#client-typed-refusals)).

The HTTP transport's `MapConduitEndpoint` returns the whole `DispatchResult` as JSON (`Results.Json`), status-coded
from `DispatchError.Status` when it's set (> 0), else falling back to `ErrorCode.ToHttpStatusCode` — a lookup over
the built-in codes table above, defaulting to 400 for an unrecognized code. That fallback only matters for a
`DispatchResult` from a server that predates `Status` being sent; every current `DispatchResult<T>.Fail` overload
sets it from the exception's (or the passed) `ErrorCode`, so custom error codes with non-standard HTTP status
resolve correctly on the envelope endpoint too — unlike RESTful endpoints, the envelope endpoint needs no fallback
caveat for them.

### Unhandled exceptions

On the envelope endpoint, a non-`ConduitException` error is first offered to the host's translators (next section). One that no translator claims is logged at Error level and returned as `InternalError` (status 500) with the fixed message `"An unexpected error occurred."`. The original message is never leaked to the caller.

The support reference is a structured field, `DispatchError.Reference` (`reference?: string` in the generated `ConduitError`), never message text. It is the dispatch's `ConversationId` when that is log-safe (`^[A-Za-z0-9-]{1,64}$`; the id is caller-supplied, so anything else could forge log lines), otherwise a minted 32-hex id, and the Error log line (EventId 1) carries the same value so support can grep for what the user quotes. Refusals carry none. An older server sends none, so a client treats the field as optional and the message still reads as a sentence on its own.

`ConduitException.Reference` (init-only, never part of the client data) lets code that wraps a failed inner dispatch (a batch item) keep the inner reference: `DispatchResult.Fail(ConduitException)` copies it onto the error. `ConduitRuntime.UnwrapReply` throws `RemoteDispatchException` (an `InvalidOperationException`) whose `Reference` is the remote error's.

### Translating an unexpected exception

`IWireExceptionTranslator` (`Conduit.Runtime`) lets a host map an exception it knows to a refusal:
`ConduitException? Translate(Exception)`. In its catch-all, before it answers `InternalError`,
`HandleWireRequest` offers the exception to every registered translator in registration order. The generated
mediator passes `serviceProvider.GetServices<IWireExceptionTranslator>()`.

- The first translator that returns a `ConduitException` wins. Its refusal goes back like any other refusal
  (status, details, client type and data), with no support reference. The use is logged at Information level
  (EventId 6, `WireExceptionTranslated`) with the original exception, because a translated exception escaped the
  pipeline and someone may want to know where.
- A translator that returns `null` passes. When every translator passes, the error becomes `InternalError` as above.
- A translator that throws is logged at Error level (EventId 5, `WireExceptionTranslatorFailed`) and skipped. The
  logged exception is an `AggregateException` holding the translator's failure, then the original. The next
  translator still gets its turn.

Typical use: a data layer's concurrency exception that escaped the pipeline becomes `PreconditionFailedException`
(412). Only the envelope endpoint consults translators. A RESTful endpoint does not (see above).

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

Previous: [Dispatch Retry](dispatch-retry.md) | [Home](../README.md) | Next: [Security](security.md)
