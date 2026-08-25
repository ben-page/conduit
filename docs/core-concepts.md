# Core Concepts

- [Overview](#overview)
- [Commands](#commands)
- [Queries](#queries)
- [Events](#events)
- [Dispatching](#dispatching)
- [Validate()](#validate)
- [Ownership model](#ownership-model)

## Overview

Conduit has three message types: commands, queries, and events. Each has a dedicated interface, handler type, and dispatch method. The source generator discovers all of them automatically.

## Commands

Commands represent operations that change state. They optionally return a result.

```csharp
using Conduit.Messages;

// Void command, returns nothing
public record DeleteTodo(Guid Id) : ICommand;

// Result-returning command
public record CreateTodo(string Title, string Body) : ICommand<Guid>;
```

Each command has exactly one handler. The handler interface matches the command's return type:

```csharp
using Conduit.Handlers;

public class DeleteTodoHandler : ICommandHandler<DeleteTodo>
{
    public Task HandleAsync(DeleteTodo command, CancellationToken cancellationToken)
    {
        // Delete the todo
    }
}

public class CreateTodoHandler : ICommandHandler<CreateTodo, Guid>
{
    public Task<Guid> HandleAsync(CreateTodo command, CancellationToken cancellationToken)
    {
        // Create the todo, return its ID
    }
}
```

The source generator enforces one-handler-per-command. Multiple handlers produce a `CONDUIT002` compile error. A command with no handler produces a `CONDUIT001` warning.

## Queries

Queries are read-only and always return data.

```csharp
using Conduit.Messages;

public record GetTodoById(Guid Id) : IQuery<TodoDto>;

public record ListTodos(int Page, int PageSize) : IQuery<PagedResult<TodoDto>>;
```

Handlers implement `IQueryHandler<TQuery, TResult>`:

```csharp
using Conduit.Handlers;

public class GetTodoByIdHandler : IQueryHandler<GetTodoById, TodoDto>
{
    public Task<TodoDto> HandleAsync(GetTodoById query, CancellationToken cancellationToken)
    {
        // Fetch and return the todo
    }
}
```

The same one-handler rule applies.

## Events

Events represent something that already happened. They use pub/sub: zero, one, or many listeners can respond to the same event. Listeners run independently. If one throws, the rest still execute.

```csharp
using Conduit.Messages;

public record TodoCompleted(Guid Id, string Title) : IEvent;
```

Listeners implement `IEventListener<TEvent>`:

```csharp
using Conduit.Handlers;

public class SendCompletionNotification : IEventListener<TodoCompleted>
{
    public Task HandleAsync(TodoCompleted @event, CancellationToken cancellationToken)
    {
        // Send a push notification
    }
}

public class UpdateCompletionStats : IEventListener<TodoCompleted>
{
    public Task HandleAsync(TodoCompleted @event, CancellationToken cancellationToken)
    {
        // Increment completion counters
    }
}
```

An event with zero listeners is valid. It has no effect locally but may still broadcast to transports.

When the [transactional outbox](outbox.md) is enabled, events published during handler execution are captured and persisted to the outbox table atomically with business data, rather than sent directly to transports.

### Where an application's own listeners run

When an application publishes an event, its local listeners run **inline on the publishing
instance**, not load-balanced across replicas. With a broker configured, the broker also routes the
application's own copy back to it; that copy is dropped on receipt, because the listeners already
ran. So an application observes its own event exactly once, on whichever instance published it. A
dropped copy is recorded as a `SelfPublishedEventSkipped` observation, so "why didn't my listener
run" has an answer in the [audit trail](audit.md) rather than in silence. See
[Transports → Self-published events](transports/index.md#self-published-events) for how the drop
works.

A heavy listener therefore stays on the publishing instance. If you want that work distributed,
publish a command routed to a worker endpoint instead of relying on the event listener.

## Dispatching

Inject `IMediator` and use the method that matches the message type:

```csharp
public class TodoService(IMediator mediator)
{
    public async Task CompleteAndNotify(Guid id, CancellationToken cancellationToken)
    {
        // Commands use ExecuteAsync
        await mediator.ExecuteAsync(new DeleteTodo(id), cancellationToken);

        // Queries use FetchAsync
        TodoDto todo = await mediator.FetchAsync(new GetTodoById(id), cancellationToken);

        // Events use PublishAsync
        await mediator.PublishAsync(new TodoCompleted(id, todo.Title), cancellationToken);
    }
}
```

The method names are intentionally distinct. `ExecuteAsync` performs an action, `FetchAsync` reads data, `PublishAsync` broadcasts. There is no ambiguous `Send` method.

## Validate()

`ICommand` and `IQuery<TResult>` have a default interface method called `Validate()`. Use it for structural precondition guards that are always invalid regardless of application state. The generated mediator calls `Validate()` before dispatch.

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

These checks are structural. A transfer of zero dollars or to `Guid.Empty` is nonsensical regardless of what accounts exist.

Validation that depends on application state belongs in the handler:

```csharp
public class TransferFundsHandler : ICommandHandler<TransferFunds>
{
    public async Task HandleAsync(TransferFunds command, CancellationToken cancellationToken)
    {
        var account = await repository.GetAsync(command.FromAccountId, cancellationToken)
            ?? throw new NotFoundException($"Account {command.FromAccountId} not found");

        if (account.Balance < command.Amount)
            throw new ConflictException(
                $"Balance {account.Balance} is less than {command.Amount}");

        // Perform the transfer
    }
}
```

Whether the account exists and whether it has sufficient funds require database state. They belong in the handler.

`IEvent` does not have `Validate()`. Events represent something that already happened.

## Ownership model

Commands and queries are owned by the service that handles them. The handler defines the contract. If an order service handles `CreateOrder`, that command type lives in the order service or a shared contracts library it publishes.

Events are owned by the service that publishes them. If the order service publishes `OrderCreated`, that event type lives there. Downstream listeners depend on it, not the other way around.

---

Previous: [Getting Started](getting-started.md) | [Home](../README.md) | Next: [Transports](transports/index.md)
