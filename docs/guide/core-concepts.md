# Core Concepts

Conduit has three message types: commands, queries, and events. Each has a dedicated interface, handler type, and dispatch method.

## Commands

Commands perform side effects. They come in two forms: void and result-returning.

```csharp
using Conduit.Messages;

// Void command — no return value
public record DeleteNote(Guid Id) : ICommand;

// Command with result — returns data after the side effect
public record CreateNote(string Title, string Body) : ICommand<Guid>;
```

Handlers implement `ICommandHandler<TCommand>` or `ICommandHandler<TCommand, TResult>`:

```csharp
using Conduit.Handlers;

public class DeleteNoteHandler : ICommandHandler<DeleteNote>
{
    public Task HandleAsync(DeleteNote command, CancellationToken cancellationToken)
    {
        // Delete the note
    }
}

public class CreateNoteHandler : ICommandHandler<CreateNote, Guid>
{
    public Task<Guid> HandleAsync(CreateNote command, CancellationToken cancellationToken)
    {
        // Create the note, return its ID
    }
}
```

The source generator discovers commands and handlers automatically. No registration attributes needed.

## Queries

Queries are read-only and always return data.

```csharp
using Conduit.Messages;

public record GetNoteById(Guid Id) : IQuery<NoteDto>;

public record ListNotes(int Page, int PageSize) : IQuery<PagedResult<NoteDto>>;
```

Handlers implement `IQueryHandler<TQuery, TResult>`:

```csharp
using Conduit.Handlers;

public class GetNoteByIdHandler : IQueryHandler<GetNoteById, NoteDto>
{
    public Task<NoteDto> HandleAsync(GetNoteById query, CancellationToken cancellationToken)
    {
        // Fetch and return the note
    }
}
```

## Events

Events are pub/sub. Zero, one, or many listeners can handle the same event. Listeners run independently with continue-on-failure semantics -- if one listener throws, the others still execute.

```csharp
using Conduit.Messages;

public record NoteCreated(Guid Id, string Title) : IEvent;
```

Listeners implement `IEventListener<TEvent>` (not "handler" -- the name avoids collision with `System.EventHandler`):

```csharp
using Conduit.Handlers;

public class SendNoteNotification : IEventListener<NoteCreated>
{
    public Task HandleAsync(NoteCreated @event, CancellationToken cancellationToken)
    {
        // Send a notification
    }
}

public class UpdateSearchIndex : IEventListener<NoteCreated>
{
    public Task HandleAsync(NoteCreated @event, CancellationToken cancellationToken)
    {
        // Index the new note
    }
}
```

## Dispatching

Inject `IMediator` and call the method that matches the message type:

```csharp
public class NotesController(IMediator mediator)
{
    public async Task CreateAndNotify(CancellationToken cancellationToken)
    {
        // Commands use ExecuteAsync
        Guid id = await mediator.ExecuteAsync(new CreateNote("Title", "Body"), cancellationToken);

        // Queries use FetchAsync
        NoteDto note = await mediator.FetchAsync(new GetNoteById(id), cancellationToken);

        // Events use PublishAsync
        await mediator.PublishAsync(new NoteCreated(id, "Title"), cancellationToken);
    }
}
```

Each method has a distinct name so the intent is unambiguous: `ExecuteAsync` performs an action, `FetchAsync` reads data, `PublishAsync` broadcasts.

## Validate()

`ICommand` and `IQuery<TResult>` have a default interface method called `Validate()`. Override it for precondition guards -- things that are always invalid regardless of application state. Business validation belongs in the handler.

```csharp
public record CreateNote(string Title, string Body) : ICommand<Guid>
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(Body);
    }
}
```

`IEvent` does not have `Validate()`. Events represent something that already happened -- there is nothing to guard against.

## Ownership model

Commands and queries are owned by the service that handles them. The handler defines the contract. If `OrderService` handles `CreateOrder`, that command type lives in `OrderService` (or a shared contracts project that `OrderService` publishes).

Events are owned by the service that publishes them. The publisher defines the contract. If `OrderService` publishes `OrderCreated`, that event type lives in `OrderService`. Downstream listeners depend on it, not the other way around.

Next: [TypeScript Client](typescript-client.md)
