# TypeScript Client

Conduit generates a fully-typed TypeScript client from your C# message types. The generated `ConduitClient` provides IDE autocomplete for type names, request shapes, and response types — no hand-written API contracts, no OpenAPI code generators, no runtime type checking.

## What you get

```typescript
import { ConduitClient, SignalRTransport } from './generated/conduit.generated';

const client = new ConduitClient(new SignalRTransport(connection));

// Type key autocompletes. Request body is typed. Response is typed.
const result = await client.execute(
  'Conduit.Sample.Notes.Commands.CreateNoteCommand',
  { text: 'Buy groceries' }
);
// result.data is string (the Guid returned by the C# handler)
// result.error is ConduitError | undefined

const notes = await client.fetch(
  'Conduit.Sample.Notes.Queries.GetNotesQuery',
  {}
);
// notes.data is NoteDto[]

// Subscribe to server-pushed events (SignalR only)
const subscription = client.on(
  'Conduit.Sample.Notes.Events.NoteCreatedEvent',
  (event) => {
    // event is typed as NoteCreatedEvent
    console.log('Note created:', event.id, event.text);
  }
);
```

`execute` and `fetch` return `DispatchResult<T>` — they never throw. Check `result.error` to handle failures. `publish` sends events fire-and-forget. `on` subscribes to server-pushed events and returns a `Subscription` with an `unsubscribe()` method.

## Mark types with `[ClientVisible]`

Any type marked `[ClientVisible]` gets a TypeScript interface in the generated output. Dispatchable types (`ICommand`, `IQuery`, `IEvent`) also appear in the generated type maps that drive `ConduitClient`'s type safety.

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

`NoteDto` is not a command, query, or event — it gets a TypeScript interface but does not appear in `CommandMap`, `QueryMap`, or `EventMap`. It is referenced by the dispatchable types that use it.

Types referenced by `[ClientVisible]` command/query properties and return types are discovered transitively. If `GetNotesQuery` returns `IReadOnlyList<NoteDto>`, `NoteDto` gets a TypeScript interface automatically even without `[ClientVisible]`. The attribute is only required on the root dispatchable types and any standalone DTOs you want exported.

## Enable TypeGen in your `.csproj`

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
| `ConduitTypeGenEnabled` | (required) | Set to `true` to enable TypeScript generation |
| `ConduitTypeGenOutput` | `ClientApp/generated` | Output directory for generated `.ts` files |
| `ConduitTransports` | `Http;SignalR` | Transports to include in `conduit.ts`. Set to `Http` to omit SignalR |

## Generated output

TypeGen emits two files:

**`conduit.ts`** — stable runtime that does not change between builds (unless you change `ConduitTransports`). Contains `DispatchResult<T>`, `ConduitError`, the `ConduitTransport` interface, `HttpTransport`, and `SignalRTransport`.

**`conduit.generated.ts`** — per-project types and client, regenerated on every build. Contains TypeScript interfaces for every `[ClientVisible]` type, the type maps, the `ConduitClient` class, and re-exports from `conduit.ts`.

The type maps use full C# namespaces as keys:

```typescript
export interface CommandMap {
  'Conduit.Sample.Notes.Commands.CreateNoteCommand': { request: CreateNoteCommand; response: string };
  'Conduit.Sample.Notes.Commands.DeleteNoteCommand': { request: DeleteNoteCommand; response: void };
}

export interface QueryMap {
  'Conduit.Sample.Notes.Queries.GetNotesQuery': { request: GetNotesQuery; response: NoteDto[] };
}

export interface EventMap {
  'Conduit.Sample.Notes.Events.NoteCreatedEvent': NoteCreatedEvent;
  'Conduit.Sample.Notes.Events.NoteDeletedEvent': NoteDeletedEvent;
}
```

These maps are what drive the type safety. When you call `client.execute('Conduit.Sample.Notes.Commands.CreateNoteCommand', ...)`, TypeScript infers the request type from `CommandMap` and the response type from the `response` field.

## Browser example with SignalR

A complete setup from connection to dispatch:

```typescript
import { HubConnectionBuilder } from '@microsoft/signalr';
import { ConduitClient, SignalRTransport } from './generated/conduit.generated';

const connection = new HubConnectionBuilder()
  .withUrl('/conduit')
  .build();

await connection.start();

const client = new ConduitClient(new SignalRTransport(connection));

// Execute a command -- returns DispatchResult<string>
const createResult = await client.execute(
  'Conduit.Sample.Notes.Commands.CreateNoteCommand',
  { text: 'Buy groceries' }
);

if (createResult.error) {
  console.error(createResult.error.code, createResult.error.message);
} else {
  console.log('Created note:', createResult.data);
}

// Fetch a query -- returns DispatchResult<NoteDto[]>
const notesResult = await client.fetch(
  'Conduit.Sample.Notes.Queries.GetNotesQuery',
  {}
);

if (notesResult.data) {
  for (const note of notesResult.data) {
    console.log(note.id, note.text);
  }
}

// Publish an event -- fire-and-forget, returns Promise<void>
await client.publish(
  'Conduit.Sample.Notes.Events.NoteCreatedEvent',
  { id: 'abc-123', text: 'Buy groceries' }
);

// Subscribe to server-pushed events -- returns Subscription
const subscription = client.on(
  'Conduit.Sample.Notes.Events.NoteCreatedEvent',
  (event) => {
    // event is typed as NoteCreatedEvent
    console.log('Note created:', event.id, event.text);
  }
);

// Later: stop listening
subscription.unsubscribe();
```

All four methods are fully typed. The type key string autocompletes and the request/response types are inferred from the type maps. `execute` and `fetch` return `DispatchResult<T>` -- they do not throw. Check `result.error` to handle failures.

## HTTP transport

For projects that don't need real-time server push, use `HttpTransport` instead:

```typescript
import { ConduitClient, HttpTransport } from './generated/conduit.generated';

// Same-origin: empty string as baseUrl
const client = new ConduitClient(new HttpTransport(''));

// Cross-origin: full URL
const client = new ConduitClient(new HttpTransport('https://api.example.com'));
```

`HttpTransport` uses the native `fetch` API with zero npm dependencies. It supports `execute`, `fetch`, and `publish`. Calling `on` throws — HTTP has no server-to-client push channel. Use `SignalRTransport` for event subscriptions.

`HttpTransport` takes two arguments: `baseUrl` (required) and `path` (optional, defaults to `'/api/dispatch'`).

## C# to TypeScript type mapping

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

Next: [Event Transport](event-transport.md)
