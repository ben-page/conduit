# Core Concepts

Conduit uses three message types — commands, queries, and events — each with a dedicated interface, handler type, and dispatch method. The source generator discovers all of them automatically.

## Commands

Commands represent operations that change state. They optionally return a result.

```csharp
using Conduit.Messages;

// Void command — performs a side effect, returns nothing
public record DeleteNote(Guid Id) : ICommand;

// Result-returning command — performs a side effect and returns data
public record CreateNote(string Title, string Body) : ICommand<Guid>;
```

Each command has exactly one handler. The handler interface matches the command's return type:

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

The source generator enforces one-handler-per-command. Multiple handlers for the same command produce a `CONDUIT002` compile error. A command with no handler produces a `CONDUIT001` warning.

## Queries

Queries are read-only operations that always return data.

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

The same one-handler rule applies. The compiler enforces the distinction between commands and queries — a handler that implements `IQueryHandler` cannot be registered against an `ICommand` type.

## Events

Events represent something that already happened. They use pub/sub: zero, one, or many listeners can respond to the same event. Listeners run independently — if one throws, the rest still execute (continue-on-failure semantics).

```csharp
using Conduit.Messages;

public record NoteCreated(Guid Id, string Title) : IEvent;
```

Listeners implement `IEventListener<TEvent>` (the name avoids collision with `System.EventHandler`):

```csharp
using Conduit.Handlers;

public class SendNoteNotification : IEventListener<NoteCreated>
{
    public Task HandleAsync(NoteCreated @event, CancellationToken cancellationToken)
    {
        // Send a push notification
    }
}

public class UpdateSearchIndex : IEventListener<NoteCreated>
{
    public Task HandleAsync(NoteCreated @event, CancellationToken cancellationToken)
    {
        // Index the new note for full-text search
    }
}
```

Unlike commands and queries, events have no handler limit. An event with zero listeners is also valid — it simply has no effect locally (though it may still broadcast to transports).

## Dispatching

Inject `IMediator` and use the method that matches the message type:

```csharp
public class NotesController(IMediator mediator)
{
    public async Task CreateAndNotify(CancellationToken cancellationToken)
    {
        // Commands use ExecuteAsync — performs an action
        Guid id = await mediator.ExecuteAsync(new CreateNote("Title", "Body"), cancellationToken);

        // Queries use FetchAsync — reads data
        NoteDto note = await mediator.FetchAsync(new GetNoteById(id), cancellationToken);

        // Events use PublishAsync — broadcasts to listeners
        await mediator.PublishAsync(new NoteCreated(id, "Title"), cancellationToken);
    }
}
```

The method names are intentionally distinct: `ExecuteAsync` performs an action, `FetchAsync` reads data, `PublishAsync` broadcasts. There is no ambiguous `Send` method that handles multiple message types.

## Validate()

`ICommand` and `IQuery<TResult>` have a default interface method called `Validate()`. Use it for structural precondition guards — invariants that are always invalid regardless of application state. The generated mediator calls `Validate()` before dispatch.

```csharp
public record TransferFunds(
    Guid FromAccountId,
    Guid ToAccountId,
    decimal Amount) : ICommand
{
    public void Validate()
    {
        if (FromAccountId == Guid.Empty)
            throw new ArgumentException("Source account is required", nameof(FromAccountId));

        if (ToAccountId == Guid.Empty)
            throw new ArgumentException("Destination account is required", nameof(ToAccountId));

        if (FromAccountId == ToAccountId)
            throw new ArgumentException("Cannot transfer to the same account");

        if (Amount <= 0)
            throw new ArgumentException("Transfer amount must be positive", nameof(Amount));
    }
}
```

These checks are structural — a transfer of zero dollars or a transfer to `Guid.Empty` is nonsensical regardless of what accounts exist in the system. Validation that depends on application state belongs in the handler:

```csharp
public class TransferFundsHandler : ICommandHandler<TransferFunds>
{
    public async Task HandleAsync(TransferFunds command, CancellationToken cancellationToken)
    {
        var account = await repository.GetAsync(command.FromAccountId, cancellationToken)
            ?? throw new NotFoundException($"Account {command.FromAccountId} not found");

        if (account.Balance < command.Amount)
            throw new InsufficientFundsException(
                $"Balance {account.Balance} is less than {command.Amount}");

        // Perform the transfer
    }
}
```

Whether the account exists and whether it has sufficient funds are business rules that require database state. They belong in the handler, not in `Validate()`.

`IEvent` does not have `Validate()`. Events represent something that already happened; there is nothing to guard against.

## Ownership model

Commands and queries are owned by the service that handles them. The handler defines the contract. If `OrderService` handles `CreateOrder`, that command type lives in `OrderService` (or a shared contracts library that `OrderService` publishes).

Events are owned by the service that publishes them. The publisher defines the contract. If `OrderService` publishes `OrderCreated`, that event type lives in `OrderService`. Downstream listeners depend on it, not the other way around.

Next: [TypeScript Client](typescript-client.md)
