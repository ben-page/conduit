# TypeScript Client

- [Overview](#overview)
- [What you get](#what-you-get)
- [Mark types with \[ClientVisible\]](#mark-types-with-clientvisible)
- [Enable TypeGen](#enable-typegen)
- [Generated output](#generated-output)
- [Browser example with SignalR](#browser-example-with-signalr)
- [HTTP transport](#http-transport)
- [Type mapping](#type-mapping)

## Overview

Conduit generates a fully-typed TypeScript client from your C# message types. The generated `ConduitClient` provides IDE autocomplete for type names, request shapes, and response types. No hand-written API contracts, no OpenAPI generators, no runtime type checking.

## What you get

```typescript
import { ConduitClient, SignalRTransport } from './generated/conduit.generated';

const client = new ConduitClient(new SignalRTransport(connection));

// Type key autocompletes. Request and response are typed.
const result = await client.execute(
  'MyApp.Commands.CreateNoteCommand',
  { text: 'Buy groceries' }
);
// result.data is string (the Guid returned by the handler)
// result.error is ConduitError | undefined

const notes = await client.fetch(
  'MyApp.Queries.GetNotesQuery',
  {}
);
// notes.data is NoteDto[]

// Subscribe to server-pushed events (SignalR only)
const sub = client.on(
  'MyApp.Events.NoteCreatedEvent',
  (event) => {
    console.log('Created:', event.id, event.text);
  }
);
```

`execute` and `fetch` return `DispatchResult<T>`. They never throw. Check `result.error` to handle failures. `publish` sends events fire-and-forget. `on` subscribes to server-pushed events and returns a `Subscription` with an `unsubscribe()` method.

## Mark types with [ClientVisible]

Any type marked `[ClientVisible]` gets a TypeScript interface. Dispatchable types also appear in the generated type maps that drive the client's type safety.

```csharp
using Conduit.Attributes;
using Conduit.Messages;

[ClientVisible]
public record CreateNoteCommand(string Text) : ICommand<Guid>;

[ClientVisible]
public record GetNotesQuery() : IQuery<IReadOnlyList<NoteDto>>;

[ClientVisible]
public record NoteCreatedEvent(Guid Id, string Text) : IEvent;

[ClientVisible]
public record NoteDto(Guid Id, string Text);
```

`NoteDto` is not a dispatchable type. It gets a TypeScript interface but does not appear in `CommandMap`, `QueryMap`, or `EventMap`.

Types referenced by `[ClientVisible]` properties and return types are discovered transitively. If `GetNotesQuery` returns `IReadOnlyList<NoteDto>`, then `NoteDto` gets a TypeScript interface automatically even without `[ClientVisible]`. The attribute is only required on root dispatchable types and standalone DTOs you want exported.

Discovery reaches through arrays, collections, a dictionary's values and `T?`, and through a generic wrapper: `IQuery<PagedResult<NoteDto>>` and a `PagedResult<TagDto>` property emit one `PagedResult<T>` interface plus `NoteDto` and `TagDto`, and a wrapper used only as a result is emitted too. It skips framework types: the namespaces `System`, `System.*` and `Microsoft.*`, and Conduit's own (`Conduit`, and `Conduit.Messages`, `Conduit.Runtime`, `Conduit.Configuration`, `Conduit.Middleware`, `Conduit.Handlers`, `Conduit.Attributes`, `Conduit.Exceptions` with their sub-namespaces). A namespace that only starts with those letters is yours and is walked like any other: `ConduitApp.*`, `SystemsInc.*`, `Conduit.Sample.*`. Referenced assemblies are walked the same way: every one but `System`, `System.*`, `Microsoft.*`, `netstandard`, `mscorlib` and `WindowsBase`.

Every interface takes its type's short name, so **two types that share a short name are a build error**: `MyApp.Orders.Item` and `MyApp.Catalog.Item` would be two `export interface Item` declarations, which TypeScript merges into one without a word, and the client would then accept either shape for both. TypeGen fails the build instead, naming both full type names; rename one. The same goes for a type named like an identifier the generated file always declares or imports (`DispatchResult`, `Subscription`, `ConduitClient`, `CommandMap`, `ErrorMap`, ...).

Every generated key of a command or query interface is **required** on the wire: the server's wire dispatch refuses a request that omits a constructor parameter, at any depth of positional models (`ContractMismatch`, see [Error handling](../error-handling.md); the RESTful endpoints stay lenient, and neither a settable property of a non-positional model nor a struct without `[JsonConstructor]` is ever checked). A C# default value on the parameter (`int Take = 50`) is how a member becomes optional on the wire; an explicit `null` is always accepted for a nullable member.

## Enable TypeGen

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ConduitTypeGenEnabled>true</ConduitTypeGenEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Conduit" />
    <PackageReference Include="Conduit.Transport.SignalR" />
    <PackageReference Include="Conduit.SourceGen" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <PackageReference Include="Conduit.Analysis" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
  <Import Project="$(NuGetPackageRoot)conduit.typegen\$(ConduitTypeGenVersion)\build\Conduit.TypeGen.targets" />
</Project>
```

TypeGen runs as a post-build MSBuild task. It scans the compiled assembly for `[ClientVisible]` types and emits TypeScript into the configured output directory.

| Property | Default | Description |
|---|---|---|
| `ConduitTypeGenEnabled` | (required) | Set to `true` to enable generation |
| `ConduitTypeGenOutput` | `ClientApp/generated` | Output directory for `.ts` files |
| `ConduitTransports` | `Http;SignalR` | Transports to include in `conduit.ts` |

## Generated output

TypeGen emits two files.

**`conduit.ts`** is the stable runtime. It contains `DispatchResult<T>`, `ConduitError`, the `ConduitTransport` interface, `HttpTransport`, and `SignalRTransport`. This file does not change between builds unless you change `ConduitTransports`. Its source is real TypeScript — `src/Conduit.TypeGen/Transport/` — embedded in `Conduit.TypeGen`.

**`conduit.generated.ts`** is regenerated on every build. It contains TypeScript interfaces for every `[ClientVisible]` type, the type maps, and the `ConduitClient` class.

The type maps use full C# namespaces as keys:

```typescript
export interface CommandMap {
  'MyApp.Commands.CreateNoteCommand': { request: CreateNoteCommand; response: string };
  'MyApp.Commands.DeleteNoteCommand': { request: DeleteNoteCommand; response: void };
}

export interface QueryMap {
  'MyApp.Queries.GetNotesQuery': { request: GetNotesQuery; response: NoteDto[] };
}

export interface EventMap {
  'MyApp.Events.NoteCreatedEvent': NoteCreatedEvent;
  'MyApp.Events.NoteDeletedEvent': NoteDeletedEvent;
}
```

These maps drive the type safety. When you call `client.execute('MyApp.Commands.CreateNoteCommand', ...)`, TypeScript infers the request type from `CommandMap` and the response type from the `response` field.

## Browser example with SignalR

A complete setup from connection to dispatch:

```typescript
import { ConduitClient, SignalRTransport } from './generated/conduit.generated';

const transport = new SignalRTransport();
const client = new ConduitClient(transport);
await transport.start();

// Execute a command
const createResult = await client.execute(
  'MyApp.Commands.CreateNoteCommand',
  { text: 'Buy groceries' }
);

if (createResult.error) {
  console.error(createResult.error.code, createResult.error.message);
} else {
  console.log('Created note:', createResult.data);
}

// Fetch a query
const notesResult = await client.fetch(
  'MyApp.Queries.GetNotesQuery',
  {}
);

if (notesResult.data) {
  for (const note of notesResult.data) {
    console.log(note.id, note.text);
  }
}

// Subscribe to events
const sub = client.on(
  'MyApp.Events.NoteCreatedEvent',
  (event) => {
    console.log('Note created:', event.id, event.text);
  }
);

// Later: stop listening
sub.unsubscribe();
```

All methods are fully typed. The type key autocompletes and request/response types are inferred from the maps.

## Connection recovery and state

**Fixed 2026-09-16** — this replaces the known defect recorded here on 2026-08-28. A consumer has the fix once it
regenerates `conduit.ts` from a `Conduit.TypeGen` that contains it.

`SignalRTransport` recovers from an outage of any length without a page reload:

- **The default retry policy never gives up.** `DEFAULT_RETRY_POLICY` (exported) waits 0 s, 2 s, 5 s, 10 s, then
  every 30 s. SignalR's stock `withAutomaticReconnect()` schedule is `[0, 2s, 10s, 30s]` and then stops for good,
  which bricked an unattended screen roughly 45 seconds into an outage. Pass `retryPolicy` in
  `SignalRTransportOptions` to use another; `automaticReconnect: false` still turns reconnection off.
- **`connect()` starts a dead connection again.** It used to memoize its resolved `start()` forever, so once the
  connection closed for good every dispatch threw *"Cannot send data if the connection is not in the 'Connected'
  State."* It now checks `connection.state` and starts again when the connection is `Disconnected`. A failed start
  is still not cached.
- **A close nobody asked for restarts itself.** `withAutomaticReconnect` only retries a connection that dropped, or
  one the server closed *with* `allowReconnect`. A server close without it, a first `start()` that fails, and a
  retry policy that gave up all used to end in `disconnected` with nothing retrying until the next dispatch. They
  now schedule a restart on the same retry policy, so a kiosk left unattended through a graceful server shutdown
  recovers exactly like one left unattended through a crash. The attempt count resets only after a connection has
  stayed up 30 seconds, so a server that accepts and immediately closes a connection is retried on the schedule,
  not in a tight loop.

`onStateChange(handler)` reports the socket's lifecycle as a `TransportConnectionState`, and calls the handler at
once with the current state:

| State | Meaning |
|---|---|
| `stopped` | Not started yet, or the app called `stop()`. Never a loss. |
| `connecting` | A start is in flight, before any connection has succeeded. |
| `connected` | Up. |
| `reconnecting` | A connection was up before; it dropped or closed and the retry policy is working on it. |
| `disconnected` | Closed, and nothing is retrying. Reachable only by a transport built with `automaticReconnect: false`, or whose retry policy itself gives up (returns `null`). |

`stop()` reports `stopped` before it closes the socket, so the close it causes is never reported as `reconnecting`
or `disconnected` — an app can tell a logout from an outage. `onStateChange` is deliberately not on
`ConduitTransport`: `HttpTransport` has no connection to report on.

**Where the behavior is tested.** The transport is real TypeScript — `src/Conduit.TypeGen/Transport/base.ts`,
`http.ts` and `signalr.ts`, embedded in `Conduit.TypeGen` and assembled into `conduit.ts` — and `tests/transport-ts`
type-checks it (strict `tsc`) and runs it under vitest, locally (`npm test`) and in CI (the `transport-ts` job): a fake
`HubConnection` driven past a permanent close, a close the server sent without `allowReconnect`, an intentional stop
and a failed start, the retry policy the restart loop is built with, and `HttpTransport`'s requests, refusals and
failures. Strict `tsc` checks each transport file, and also the two assembled outputs checked in under
`tests/transport-ts/emitted/` — the default `conduit.ts` and the HTTP-only one, the only two shapes it takes (the
configured paths change only literals). A `BaseFileEmitterShould` test pins both to the emitter, so a transport edit fails
until the goldens are regenerated, and the golden diff is what every consumer's `conduit.ts` will show. Beside them,
`conduit.generated.ts` is the client TypeGen's own test types produce (the shapes real contracts take: generic
wrappers, arrays, sets, non-string dictionary keys, the System.Text.Json attributes); `EmittedGoldenShould` pins it to
the emitter and strict `tsc` checks it against the default `conduit.ts`, so an emitter change that writes invalid
TypeScript fails CI.
ManagementSystem keeps its own copy of the SignalR suite, run against its committed `conduit.ts`. These tests protect a
consumer only for a `Conduit.TypeGen` built from this source: GtlHmi pins 1.0.1 (next paragraph).

**Who has this fix.** ManagementSystem regenerated `conduit.ts` against it and observed a kiosk recover from two
five-minute outages unattended. **GtlHmi does not have it yet.** It pins `Conduit.TypeGen` 1.0.1 from NuGet, so it
keeps the old behavior until it takes a package that contains this fix — and its `StartAwareTransport` subclass
overrides a `start()` method the current emitter no longer emits in the same shape, so that upgrade needs its own
look, not a version bump alone.

## Server identity

`SignalRTransport.onServerInfo(handler)` registers a handler for the `ConduitServerInfo` a host sends on every
connect and reconnect (`docs/transports/signalr.md` → "Server identity on connect").
- It does **not** start the connection, so a consumer can register it at construction — before anyone signs in —
  and still hear the first connect.
- It is not on `ConduitTransport`, because `HttpTransport` has no connection.

## HTTP transport

For projects that do not need real-time push, use `HttpTransport`:

```typescript
import { ConduitClient, HttpTransport } from './generated/conduit.generated';

// Same-origin
const client = new ConduitClient(new HttpTransport(''));

// Cross-origin
const client = new ConduitClient(new HttpTransport('https://api.example.com'));
```

`HttpTransport` uses the native `fetch` API with zero npm dependencies. It supports `execute`, `fetch`, and `publish`. Calling `on` throws because HTTP has no server-to-client push. Use `SignalRTransport` for event subscriptions.

`HttpTransport` takes two arguments: `baseUrl` (required) and `basePath` (optional, defaults to `'/api/conduit'`). The transport appends `/dispatch` and `/publish` to the base path.

## Wire format

Both transports send the plain serialized message as the payload — no envelope wrapper.

**HTTP**: The message type is sent in the `X-Conduit-Type` header. The request body contains only the serialized message JSON.

**SignalR**: The message type and payload are separate hub method arguments. The third argument is a context dictionary (`null` from browser clients).

## MessageContext

Browser clients are always the origin of a new conversation. They do not send `MessageContext` ids — the server creates a fresh context (`MessageId`, `ConversationId`, `CausationId`) when it receives the request. All downstream dispatches (to other services via broker transports) inherit this context through the causal chain.

Both halves of that hold in code. The client never sends the ids: `HttpTransport` drops `X-Conduit-Message-Id`, `X-Conduit-Conversation-Id` and `X-Conduit-Causation-Id` from the headers you pass it (in any letter case), and `SignalRTransport` passes `null` as the hub's context argument. And the server does not take them from a browser: the HTTP endpoint and the hub ignore caller-supplied ids unless they opt in for service-to-service callers, which an edge a browser can reach should not do ([HTTP → Correlation ids](http.md#correlation-ids), [SignalR → Correlation ids](signalr.md#correlation-ids)), so a hand-written request cannot set them either.

C# service-to-service transports (HTTP, SignalR, brokers) send `MessageContext` automatically. HTTP uses `X-Conduit-Message-Id`, `X-Conduit-Causation-Id`, `X-Conduit-Conversation-Id` headers. SignalR uses a context dictionary as the third hub method argument. Broker transports use transport-native message properties, and always keep them. Over HTTP and SignalR the receiving edge keeps them only when it trusts its callers.

## Type mapping

Each type maps to what System.Text.Json writes for it with Conduit's wire options (`ConduitRuntime.JsonOptions`).

| C# | TypeScript |
|---|---|
| `string`, `char`, `Guid`, `Uri` | `string` |
| `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `Int128`, `UInt128`, `Half`, `float`, `double`, `decimal` | `number` (a JavaScript number holds integers exactly only up to 2^53) |
| `bool` | `boolean` |
| `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan` | `string` |
| `T[]`, `List<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, `ICollection<T>`, `HashSet<T>`, `ISet<T>`, `IReadOnlySet<T>`, `SortedSet<T>`, `Queue<T>`, `Stack<T>`, `LinkedList<T>`, `Collection<T>`, `ObservableCollection<T>`, `ReadOnlyCollection<T>`, `ImmutableArray<T>`, `ImmutableList<T>`, `ImmutableHashSet<T>`, `ImmutableSortedSet<T>`, `ImmutableQueue<T>`, `ImmutableStack<T>`, `FrozenSet<T>`, `ConcurrentBag<T>`, `ConcurrentQueue<T>`, `ConcurrentStack<T>` | `T[]` |
| `byte[]` | `string` (base64, as System.Text.Json writes it; a `List<byte>` stays `number[]`) |
| `Dictionary<string, T>`, `IDictionary<string, T>`, `IReadOnlyDictionary<string, T>`, `SortedDictionary<string, T>`, `SortedList<string, T>`, `ReadOnlyDictionary<string, T>`, `ImmutableDictionary<string, T>`, `ImmutableSortedDictionary<string, T>`, `FrozenDictionary<string, T>`, `ConcurrentDictionary<string, T>` | `Record<string, T>` |
| any of those dictionaries with a key that is not `string` (`Dictionary<int, T>`, `Dictionary<bool, T>`, an enum key) | `Record<string /* int */, T>`: a JSON object's keys are always strings (`"1"`, `"True"`, the enum member's name), so the key is `string`, and the comment names the C# key type |
| `T?` (nullable) | `T \| null` |
| `enum` | string literal union of what the wire carries: each member's name, or its `[JsonStringEnumMemberName]` (e.g. `'Active' \| 'in-progress'`) |
| `[Flags]` enum | `string`: a combination is written as `"Read, Write"`, which no union of member names covers |
| a closed set (`IClosedSet<TSelf>` + `[ClosedSet("a", "b")]`) | string literal union of its values (`'a' \| 'b'`) |
| a generic type (`PagedResult<T>`) | one `PagedResult<T>` interface, used as `PagedResult<NoteDto>` |
| `object`, `JsonElement` | `unknown` |

### System.Text.Json attributes

The wire honors these, so the generated interfaces do too:

| Attribute | Generated property |
|---|---|
| `[JsonPropertyName("order_no")]` | keyed `order_no` instead of the camelCased name; a name that is not a TypeScript identifier is quoted (`'x-trace': string`) |
| `[JsonIgnore]` | left out |
| `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` (or `WhenWritingDefault`, `WhenWriting`, `WhenReading`) | optional (`note?: string \| null`), since the key is missing in one direction or for some values |
| `[JsonIgnore(Condition = JsonIgnoreCondition.Never)]` | unchanged |

`[JsonStringEnumMemberName]` and `[Flags]` change an enum's type as the table above says. Put `[JsonPropertyName]` and
`[JsonIgnore]` on the property, as System.Text.Json reads them: on a positional record that is
`[property: JsonPropertyName("order_no")]`.

### Closed sets

A closed set is a class whose constructor is private, so every instance is one of a fixed list — a string-valued enum
whose spellings need not be C# identifiers. Declare it as `IClosedSet<TSelf>` and state its values again in
`[ClosedSet]`, in `All`'s order (TypeGen reads metadata and cannot run the initializers that create the members):

```csharp
[ClosedSet("self", "reports", "any")]
public sealed record Reach : IClosedSet<Reach>
{
    public static readonly Reach Self = new("self");
    public static readonly Reach Reports = new("reports");
    public static readonly Reach Any = new("any");

    private Reach(string value) => this.Value = value;

    public static IReadOnlyList<Reach> All { get; } = [Self, Reports, Any];

    public string Value { get; }
}
```

A `Reach` property emits as `'self' | 'reports' | 'any'`. On the wire a member is its bare `Value`; any other string, a
number or an object is refused at the wire read as a `BadRequest` naming its JSON path, and a JSON `null` binds as
`null` — refuse it in `Validate()` where the member is required. Conduit throws the first time it serializes the type if
`[ClosedSet]` and `All` differ, so a test that round-trips each member pins the generated client to the wire. The envelope and REST endpoints (`MapConduitGet`/`MapConduitPost`) read and write with Conduit's wire JSON options, so
a closed set or enum needs no converter registered on the host.

---

Previous: [SignalR](signalr.md) | [Home](../../README.md) | Next: [HTTP](http.md)
