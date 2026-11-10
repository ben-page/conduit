# Error Handling

- [Overview](#overview)
- [ErrorCode](#errorcode)
  - [ErrorCode equality](#errorcode-equality)
- [Built-in exceptions](#built-in-exceptions)
- [Custom error codes](#custom-error-codes)
- [Error flow](#error-flow)
  - [RESTful endpoints](#restful-endpoints)
  - [Envelope endpoint (wire dispatch)](#envelope-endpoint-wire-dispatch)
  - [Unhandled exceptions](#unhandled-exceptions)
  - [Translating an unexpected exception](#translating-an-unexpected-exception)
  - [Wire events](#wire-events)
- [Advanced](#advanced)
  - [ContractMismatch: a request missing a member](#contractmismatch-a-request-missing-a-member)
  - [Client-typed refusals](#client-typed-refusals)
  - [A refusal in the reader's language](#a-refusal-in-the-readers-language)

## Overview

Conduit provides a structured error model that works across all transports. A handler refuses a request by throwing
a `ConduitException`, which carries an `ErrorCode`. Conduit translates it into Problem Details for REST endpoints, a
`DispatchResult` error envelope for wire dispatch, and a log entry for a fire-and-forget event. Any other exception is
an unexpected failure: the caller is told only that one happened, with a support reference.

## ErrorCode

`ErrorCode` (`Conduit.Exceptions`) pairs a machine-readable string with an HTTP status code:

```csharp
public record ErrorCode
{
    public ErrorCode(string value, int httpStatusCode);

    public string Value { get; }
    public int HttpStatusCode { get; }
}
```

Built-in codes:

| Code | Value | HTTP Status |
|---|---|---|
| `ErrorCode.BadRequest` | `"BadRequest"` | 400 |
| `ErrorCode.ContractMismatch` | `"ContractMismatch"` | 400 |
| `ErrorCode.Unauthorized` | `"Unauthorized"` | 401 |
| `ErrorCode.Forbidden` | `"Forbidden"` | 403 |
| `ErrorCode.NotFound` | `"NotFound"` | 404 |
| `ErrorCode.Conflict` | `"Conflict"` | 409 |
| `ErrorCode.PreconditionFailed` | `"PreconditionFailed"` | 412 |
| `ErrorCode.InternalError` | `"InternalError"` | 500 |

### ErrorCode equality

`ErrorCode` equality is based on `Value` only. Two instances with the same `Value` but different `HttpStatusCode` are considered equal:

```csharp
var a = new ErrorCode("InsufficientCredit", 422);
var b = new ErrorCode("InsufficientCredit", 400);

a == b  // true
```

`Value` is the wire identity. It appears in `DispatchError.Code` and travels across service boundaries. `HttpStatusCode` is a local concern used only for HTTP responses.

## Built-in exceptions

Each maps to its `ErrorCode`:

```csharp
throw new BadRequestException("Quantity must be positive");                                    // 400
throw new UnauthorizedException("API key is invalid");                                         // 401
throw new ForbiddenException("Only a manager can approve a refund");                           // 403
throw new NotFoundException("Order 42 was not found");                                         // 404
throw new ConflictException("Order was already shipped");                                      // 409
throw new PreconditionFailedException("Order was modified by someone else. Refresh and try again."); // 412
```

`ConflictException` is for the resource's current state refusing the request — a refresh would show the
same refusal. `PreconditionFailedException` is for a stale version: the caller's ETag no longer matches,
and a refresh gives them a live one to retry with.

Conduit throws some of them itself. The [type filter](security.md) refuses a message type it does not allow with
`ForbiddenException`. A wire message whose type the host does not know, or whose body its type cannot read, is
refused with `BadRequestException`, and a wire request missing a member with `ContractMismatchException`
([below](#contractmismatch-a-request-missing-a-member)).

Each is a public class deriving from `ConduitException`, and none is sealed. A class you derive from one keeps its
code; declared in your own assembly, it is also a [client-typed refusal](#client-typed-refusals). `ConduitException`
itself is abstract:

```csharp
public abstract class ConduitException : Exception
{
    protected ConduitException(ErrorCode code, string message, IReadOnlyList<DispatchErrorDetail>? details = null);

    public ErrorCode Code { get; }
    public IReadOnlyList<DispatchErrorDetail>? Details { get; }   // structured operands, e.g. one per invalid field
    public string? Reference { get; init; }                        // a support reference to carry, see below
    public RefusalText? Text { get; init; }                        // the sentence's name, see below
}
```

`details` is a list of `DispatchErrorDetail(Field, Message)` (`Conduit.Runtime`). The envelope endpoint and the
SignalR hub send them with the refusal; a RESTful endpoint does not ([below](#restful-endpoints)).

## Custom error codes

Define your own `ErrorCode` and exception for domain-specific errors:

```csharp
using Conduit.Exceptions;

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

A RESTful endpoint (`MapConduitGet`, `MapConduitPost` and the rest) catches the `ConduitException` and returns an
RFC 9457 Problem Details response, built from the exception's status and message **only**:

```
Handler throws ConduitException
  -> Results.Problem(detail: ex.Message, or ex.Text written in the reader's language, statusCode: ex.Code.HttpStatusCode, title: ex.Code.Value)
```

`detail` is `ex.Message`, or the refusal's `Text` written in the reader's language when it names its sentence
(see [A refusal in the reader's language](#a-refusal-in-the-readers-language)).

The `HttpStatusCode` from `ErrorCode` is used directly, so custom error codes like `InsufficientCredit` with status
422 work automatically. But the response never carries `ex.Details`, `ex.GetClientType()` or `ex.GetClientData()`:
a typed refusal's `Type` and `Data` (see [Client-typed refusals](#client-typed-refusals)), and any structured
`Details`, do **not** reach a RESTful caller. A screen that needs to narrow on a refusal's type or read its data
must go through the envelope endpoint below.

### Envelope endpoint (wire dispatch)

`ConduitRuntime.HandleWireRequest` catches the exception and wraps it in a `DispatchResult`, which carries the
refusal's status, and — for a client-typed refusal — its type name and data too:

```
Handler throws ConduitException
  -> DispatchResult.Fail(ex)
     -> DispatchError { Code, Message, Details, Status, Type, Data }  (and Text, never serialized)
  -> RefusalRendering.RenderResult(result, services, MessageContext.Current)   (the endpoint; the hub does the same)
     -> Message and each detail's Message written in the reader's language when they carry Text
```

`Type` and `Data` are set only when `ConduitException.IsClientTyped` is true for the exception's runtime type;
otherwise both are `null`. When `Type` is set, `Data` is never `null` on the wire — a data-less typed refusal still
sends `{}` (see [Client-typed refusals](#client-typed-refusals)).

`HandleWireRequest` answers in English and carries the sentence's name in `DispatchError.Text`. The envelope endpoint
and the hub then write `Message` and each detail's `Message` in the reader's language
([A refusal in the reader's language](#a-refusal-in-the-readers-language)); the exception itself is not changed.

The HTTP transport's `MapConduitEndpoint` returns the whole `DispatchResult` as JSON (`Results.Json`), status-coded
from `DispatchError.Status` when it's set (> 0), else falling back to `ErrorCode.ToHttpStatusCode` — a lookup over
the built-in codes table above, defaulting to 400 for an unrecognized code. That fallback only matters for a
`DispatchResult` from a server that predates `Status` being sent; every current `DispatchResult<T>.Fail` overload
sets it from the exception's (or the passed) `ErrorCode`, so custom error codes with non-standard HTTP status
resolve correctly on the envelope endpoint too — unlike RESTful endpoints, the envelope endpoint needs no fallback
caveat for them.

### Unhandled exceptions

On the envelope endpoint, a non-`ConduitException` error is first offered to the host's translators (next section). One that no translator claims is logged at Error level and returned as `InternalError` (status 500) with the fixed message `"An unexpected error occurred."`. The original message is never leaked to the caller. That message is named `ConduitTextNames.UnexpectedError` (carried in `DispatchError.Text`), so the edge can write it from a host's catalog in the reader's language.

The support reference is a structured field, `DispatchError.Reference` (`reference?: string` in the generated `ConduitError`), never message text. It is the dispatch's `ConversationId` when that is log-safe (`^[A-Za-z0-9-]{1,64}$`; the id is caller-supplied, so anything else could forge log lines), otherwise a minted 32-hex id, and the Error log line (EventId 1) carries the same value so support can grep for what the user quotes. Refusals carry none. An older server sends none, so a client treats the field as optional and the message still reads as a sentence on its own.

**An unknown type is a refusal.** A wire request whose type the host neither handles nor routes is answered `BadRequest` (400) with the message `"Unknown message type '<name>'."`: the caller chose the name, so it is the caller's mistake, not a server error. It carries no support reference and is logged at Warning without a stack trace (EventId 8, `UnknownMessageType`), so a stream of made-up type names cannot fill the log with Error entries. `PreflightWireRequestAsync` answers an unknown name the same way, and an unknown event type on `/publish` or the hub is refused the same way too (see [Wire events](#wire-events)).

**A caller's cancellation is not an error.** When the caller's own cancellation token fires mid-dispatch (an HTTP client disconnects, so `RequestAborted` is cancelled, or a hub connection closes), `HandleWireRequest` rethrows the `OperationCanceledException`. It writes no answer (nobody is waiting for one), no Error log entry and no support reference, and the dispatch's activity is marked as failed. An `OperationCanceledException` thrown while the caller's token is still live, such as a handler's own timeout, is an unexpected failure like any other.

`ConduitException.Reference` (init-only, never part of the client data) lets code that wraps a failed inner dispatch (a batch item) keep the inner reference: `DispatchResult.Fail(ConduitException)` copies it onto the error. `ConduitRuntime.UnwrapReply` throws `RemoteDispatchException` (an `InvalidOperationException`) whose `Reference` is the remote error's and whose `Code` is the remote error's `ErrorCode`, with the status its server declared: `Code == ErrorCode.InternalError` tells a remote unexpected failure from a refusal.

### Translating an unexpected exception

`IWireExceptionTranslator` (`Conduit.Runtime`) lets a host map an exception it knows to a refusal:
`ConduitException? Translate(Exception)`. In its catch-all, before it answers `InternalError`,
`HandleWireRequest` offers the exception to every registered translator in registration order. The generated
mediator hands `HandleWireRequest` its service provider, and the runtime resolves the translators from it.

- The first translator that returns a `ConduitException` wins. Its refusal goes back like any other refusal
  (status, details, client type and data, and its `Text` for the edge to write in the reader's language), with no support reference. The use is logged at Information level
  (EventId 6, `WireExceptionTranslated`) with the original exception, because a translated exception escaped the
  pipeline and someone may want to know where.
- A translator that returns `null` passes. When every translator passes, the error becomes `InternalError` as above.
- A translator that throws is logged at Error level (EventId 5, `WireExceptionTranslatorFailed`) and skipped. The
  logged exception is an `AggregateException` holding the translator's failure, then the original. The next
  translator still gets its turn.

Typical use: a data layer's concurrency exception that escaped the pipeline becomes `PreconditionFailedException`
(412). Only the envelope endpoint consults translators. A RESTful endpoint does not (see above).

### Wire events

Wire events are fire-and-forget, but a refusal still reaches the publisher. When a gate refuses an event (the type
filter's `ForbiddenException`) or its type is unknown (`BadRequest`), `HandleWireEvent` logs it at Warning without a
stack trace (EventId 7, `RefusedEvent`) and rethrows it. HTTP `/publish` answers it with the `DispatchResult`
envelope, status-coded as `/dispatch` is, and the hub's `Publish` fails with a `HubException`. Either way the message is
written in the reader's language. A broker transport acknowledges the refused message. A cancellation of the caller's
token propagates as it does for a request.

Any other failure is unexpected, whether a listener threw (`EventListenerException`: every listener still ran, and
each failure is logged as `ListenerFailed`) or the dispatch failed outside the listeners (a behavior, listener
resolution, a gate that throws something other than a refusal). The publisher hears an unexpected failure, never the
exception. HTTP `/publish` answers the `InternalError` envelope: 500, the fixed message and the support reference,
which the C# `HttpTransport` throws as a `RemoteDispatchException` with code `InternalError`. The hub's `Publish` fails
with a `HubException` carrying the fixed message. The reference is in the Error line (EventId 2, `PublishError`) that
carries the exception: the first listener's failure, or the failure outside the listeners. A broker transport has no
publisher waiting, so it captures the message or abandons it to the broker for retry or dead-lettering.

## Advanced

### ContractMismatch: a request missing a member

An inbound wire request that is missing a primary-constructor parameter is refused with `ContractMismatchException`
(code `"ContractMismatch"`, status 400). It means the sender was built against an older contract -- typically an
out-of-date browser tab -- and would otherwise silently default (clear) a field it does not know about. The check is
always on; there is no opt-in flag.

- `Message`: `"This page is out of date: the request is missing {names}. Reload to continue."` (names joined `, `). At
  most 20 members are named; the rest are counted (`… and 55 more.`), so a large payload cannot make a large
  error. It declares no data property, so it stays a plain (not client-typed) refusal: `Type`/`Data` are null.
- `Text`: `ConduitTextNames.ContractMismatch` with the value `members` (the same `, `-joined names), or
  `ConduitTextNames.ContractMismatchMore` with `members` and `more` (how many were only counted), so a host can
  write it in the reader's language (see [A refusal in the reader's language](#a-refusal-in-the-readers-language)).
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
  (`ConduitRestHandlers`' `ReadFromJsonAsync`, with `ConduitRuntime.JsonOptions`) and the multipart binder stay lenient:
  a REST request missing a member still defaults it. The shared `ConduitRuntime.JsonOptions` stays lenient because it
  also reads stored `Conduit.Jobs` payloads, remote replies and events: a job stored before a member was added must
  keep running.
- A request with any other fault (wrong-typed field, integer enum ordinal) stays a `BadRequest`, even if a member is also missing.

### Client-typed refusals

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

### A refusal in the reader's language

A refusal's `Message` is English, and it stays English inside the server: logs, `Activity` status, audit and the
exception itself keep it. A throw site that wants the refusal read in the reader's language also names its sentence:

```csharp
throw new ConflictException("Order 42 was already shipped")
{
    Text = new RefusalText("orders.shipped", new Dictionary<string, object?> { ["order"] = 42 }),
};
```

- **`RefusalText(Name, Values)`** (`Conduit.Exceptions`) names the sentence in the host's catalog and the values that
  fill it. `ConduitException.Text` carries it for the refusal, `DispatchErrorDetail.Text` for one detail. A value
  that is itself a word (a field's label, say) is a **`TextName`**, which the catalog translates before it fills the
  sentence; passed as English text, it would sit untranslated inside the Spanish sentence.
- **Nothing new crosses the wire.** `ConduitException.Text` is declared on the base class, which `ClientDataProperties`
  never walks, and `DispatchErrorDetail.Text` is `[JsonIgnore]`. `DispatchError.Message` and each detail's `Message`
  are still one string each, already written in the reader's language. TypeGen's `ErrorMap` and the generated
  TypeScript do not change.
- **The host registers two services.** Conduit owns no catalog and no list of cultures.
  - `IDispatchCultureResolver` (`Conduit.Runtime`): `CultureInfo? Resolve(MessageContext? context)` answers the
    reader's culture, or null for English. It reads the dispatch's `MessageContext`, whose `Properties` are where an
    `IDispatchContextEnricher` puts server-trusted values on every transport. `DispatchPropertyCultureResolver(propertyName,
    cultures)` is the ready-made one: one property and a fixed map of its values to cultures, matched
    case-insensitively. Anything else (no context, no property, a non-string, an unmapped value) answers null. It never
    builds a `CultureInfo` from the value.
  - `IRefusalTextRenderer` (`Conduit.Runtime`): `string? Render(RefusalText text, CultureInfo culture)` answers the
    sentence in that culture, or null to keep the throw site's English. Conduit calls it for every culture the
    resolver answers, English included, so a host that answers null for English keeps one source of English: the
    throw site.
- **Three edges write it, once, where an answer leaves the server for a person.** The envelope endpoint
  (`MapConduitEndpoint`) and the SignalR hub (`ConduitHub.Dispatch`) call `RefusalRendering.RenderResult` on the
  `DispatchResult` they are about to send; a RESTful endpoint's `detail` goes through `RefusalRendering.RenderMessage`.
  Everything in process stays English: a caller of `HandleWireRequestAsync` or `PreflightWireRequestAsync` (a host's
  batch, a preflight) gets the English message with `DispatchError.Text` carrying the sentence's name and values, and
  each detail keeps its own `Text`. A host that hands such an answer to a person either calls `RenderResult` at its own
  edge or rethrows a refusal carrying the `Text`, so the answer is written once. `DispatchError.Text` is `[JsonIgnore]`
  like the others, so a broker reply is English. The generated mediator hands `HandleWireRequest` its
  `IServiceProvider` (the `HandleWireRequest(..., ILogger logger, IServiceProvider services, CancellationToken)`
  overload), and the runtime resolves only the translators from it. The renderer and resolver are looked up at the
  edge, and only when there is text to render: a success, or a failure with no `Text`, makes no lookup.
- **Conduit never sets `CultureInfo.CurrentCulture` or `CurrentUICulture`,** so dates and numbers in server-side text
  keep their format. Host code that needs the reader's language for something else, such as a notification or an
  email, calls the same resolver with `MessageContext.Current`.
- **The answer is English, byte-identical to a refusal without `Text`,** whenever no resolver or renderer is
  registered, the resolver answers null or throws, the refusal or detail has no `Text`, or the renderer answers null,
  empty or whitespace, or throws, or a resolver or renderer cannot be resolved from DI. Each detail renders on its own: one that fails stays English and the others still
  render. A failure is logged at Error, under `RefusalRendering`'s category: EventId 20 `RefusalTextRenderFailed`
  (with the text's name, the culture and the renderer's exception) and EventId 21 `DispatchCultureResolveFailed`.
- **A client can set the property when no enricher writes it.** `ConduitHub` seeds a dispatch's properties with the
  client's `headers`, and an HTTP wire endpoint mapped with `readClientProperties: true` with its
  `X-Conduit-Property-*` headers ([HTTP → Client properties](transports/http.md#client-properties)), and an enricher's
  writes win only for the keys it writes (names are case-insensitive). So an enricher that owns the language key must write it, or remove the client's, on every dispatch, and a
  resolver must accept only values it maps, as `DispatchPropertyCultureResolver` does. REST endpoints and
  `WithConduitDispatchContext()` routes read no client properties.
- **Conduit names its own two sentences** (`ConduitTextNames`), so a host's catalog can translate them:
  `conduit.unexpectedError` (the `InternalError` answer, no values), and `conduit.contractMismatch` (value `members`)
  or `conduit.contractMismatchMore` (values `members` and `more`) for a [ContractMismatch](#contractmismatch-a-request-missing-a-member).

---

Previous: [Dispatch Retry](dispatch-retry.md) | [Home](../README.md) | Next: [Security](security.md)
