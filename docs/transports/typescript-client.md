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

**`conduit.ts`** is the stable runtime. It contains `DispatchResult<T>`, `ConduitError`, the `ConduitTransport` interface, `HttpTransport`, and `SignalRTransport`. This file does not change between builds unless you change `ConduitTransports`.

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

## Known defect — the SignalR transport wedges after a permanent close

**Status: open as of 2026-08-28. Affects every consumer of the generated `SignalRTransport`.**

`connect()` memoises `this.started`. Once `start()` resolves, that promise stays resolved for the
life of the object:

```typescript
connect(): Promise<void> {
  if (!this.started) this.started = this.connection.start().catch(e => { this.started = undefined; throw e; });
  return this.started;
}
```

`withAutomaticReconnect()` is emitted with no arguments, so it uses SignalR's stock
`[0, 2s, 10s, 30s]` schedule and then **stops retrying for good**. After that, `connect()` returns
the stale resolved promise, `invoke()` runs against a closed connection, and every dispatch throws:

```
Cannot send data if the connection is not in the 'Connected' State.
```

Nothing clears `started` except `stop()`. In practice that means **an app that loses its network for
roughly 45 seconds is dead until the page is reloaded** — which is fine for a desktop tab someone is
sitting in front of, and not fine at all for an unattended screen.

Two fixes, both in `Conduit.TypeGen/BaseFileEmitter.cs` (`EmitSignalRTransport`):

1. `connect()` must clear `started` when `connection.state === HubConnectionState.Disconnected`,
   so a dispatch restarts a dead connection instead of wedging on it.
2. The default retry policy should be unbounded with a capped interval, with the stock schedule
   available via an explicit option. An unattended client must recover after an outage of any length.

Worked design and a task-by-task fix, including a behavioural regression test driving a fake
`HubConnection` past a permanent close:
`ManagementSystem/docs/superpowers/plans/2026-08-28-connection-awareness.md` (Tasks 1–3).

**Why the fix needs a test outside this repo.** The transport is emitted as C# string literals and
there is no TypeScript test project here, so `Conduit.TypeGen.Tests` can only assert on emitted
*text* — a test that greps the output for a line passes whether or not the logic around it is
correct. Giving the sample `ClientApp` a real test runner, or extracting the transport into a
hand-written TS package the emitter references, would let this be tested at its source.

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

Browser clients are always the origin of a new conversation. They do not send `MessageContext` headers — the server creates a fresh context (`MessageId`, `ConversationId`) when it receives the request. All downstream dispatches (to other services via broker transports) inherit this context through the causal chain.

C# service-to-service transports (HTTP, SignalR, brokers) propagate `MessageContext` automatically. HTTP uses `X-Conduit-Message-Id`, `X-Conduit-Causation-Id`, `X-Conduit-Conversation-Id` headers. SignalR uses a context dictionary as the third hub method argument. Broker transports use transport-native message properties.

## Type mapping

| C# | TypeScript |
|---|---|
| `string`, `Guid` | `string` |
| `int`, `long`, `float`, `double`, `decimal` | `number` |
| `bool` | `boolean` |
| `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly` | `string` |
| `T[]`, `List<T>`, `IReadOnlyList<T>` | `T[]` |
| `Dictionary<string, T>` | `Record<string, T>` |
| `T?` (nullable) | `T \| null` |
| `enum` | string literal union (e.g. `'Active' \| 'Inactive'`) |

---

Previous: [SignalR](signalr.md) | [Home](../../README.md) | Next: [HTTP](http.md)
