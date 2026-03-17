# TypeScript Client

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
import { HubConnectionBuilder } from '@microsoft/signalr';
import { ConduitClient, SignalRTransport } from './generated/conduit.generated';

const connection = new HubConnectionBuilder()
  .withUrl('/conduit')
  .build();

await connection.start();

const client = new ConduitClient(new SignalRTransport(connection));

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

`HttpTransport` takes two arguments: `baseUrl` (required) and `path` (optional, defaults to `'/api/dispatch'`).

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

Next: [HTTP](http.md)
