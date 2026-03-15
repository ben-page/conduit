# TypeScript Client

## Mark types with `[ClientVisible]`

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

Dispatchable types (`ICommand`, `IQuery`, `IEvent`) appear in the generated `CommandMap`, `QueryMap`, or `EventMap`. Non-dispatchable types like `NoteDto` get a TypeScript interface but are not added to any type map -- they are referenced by the dispatchable types that use them.

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

TypeGen runs after every build. It scans the compiled assembly for `[ClientVisible]` types and emits TypeScript.

## MSBuild properties

| Property | Default | Description |
|---|---|---|
| `ConduitTypeGenEnabled` | (required) | Set to `true` to enable TypeScript generation |
| `ConduitTypeGenOutput` | `ClientApp/generated` | Output directory for generated `.ts` files |
| `ConduitTransports` | `Http;SignalR` | Transports to include in `conduit.ts`. Set to `Http` to omit SignalR |

## Generated output

TypeGen emits two files into the output directory:

**`conduit.ts`** -- stable runtime, not project-specific. Contains `DispatchResult<T>`, `ConduitError`, `ConduitTransport` interface, `HttpTransport`, and `SignalRTransport`. Does not change between builds unless you change `ConduitTransports`.

**`conduit.generated.ts`** -- per-project types and client. Contains TypeScript interfaces for every `[ClientVisible]` type, the `CommandMap`/`QueryMap`/`EventMap` type maps, the `ConduitClient` class, and re-exports from `conduit.ts`.

The generated type maps use the full C# namespace as keys:

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

## Browser example with SignalR

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

For HTTP-only projects, or when you do not need server-pushed events:

```typescript
import { ConduitClient, HttpTransport } from './generated/conduit.generated';

// Same-origin: empty string as baseUrl
const client = new ConduitClient(new HttpTransport(''));

// Cross-origin: full URL
const client = new ConduitClient(new HttpTransport('https://api.example.com'));
```

`HttpTransport` takes two arguments: `baseUrl` (required) and `path` (optional, defaults to `'/api/dispatch'`). It uses the native `fetch` API with zero npm dependencies.

`HttpTransport` supports `execute`, `fetch`, and `publish`. Calling `on` throws -- HTTP has no server-to-client push. Use `SignalRTransport` for event subscriptions.

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
