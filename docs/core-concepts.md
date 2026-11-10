# Core Concepts

- [Overview](#overview)
- [Commands](#commands)
- [Queries](#queries)
- [Events](#events)
- [Dispatching](#dispatching)
  - [Dispatch options](#dispatch-options)
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

Each command has exactly one handler. The handler interface matches the command's return type. In these
samples, `TodoStore` stands in for your own data access:

```csharp
using Conduit.Handlers;

public class DeleteTodoHandler(TodoStore store) : ICommandHandler<DeleteTodo>
{
    public Task HandleAsync(DeleteTodo command, CancellationToken cancellationToken)
        => store.DeleteAsync(command.Id, cancellationToken);
}

public class CreateTodoHandler(TodoStore store) : ICommandHandler<CreateTodo, Guid>
{
    public async Task<Guid> HandleAsync(CreateTodo command, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await store.AddAsync(new TodoDto(id, command.Title, command.Body), cancellationToken);
        return id;
    }
}
```

Handlers are resolved from DI for each dispatch, so they take their dependencies through the constructor.

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
using Conduit.Exceptions;
using Conduit.Handlers;

public class GetTodoByIdHandler(TodoStore store) : IQueryHandler<GetTodoById, TodoDto>
{
    public async Task<TodoDto> HandleAsync(GetTodoById query, CancellationToken cancellationToken)
        => await store.FindAsync(query.Id, cancellationToken)
            ?? throw new NotFoundException($"Todo {query.Id} was not found");
}
```

A handler refuses a request by throwing one of Conduit's exceptions, here `NotFoundException`: the caller gets
the refusal and its message, with status 404 over HTTP ([Error Handling](error-handling.md)).

The same one-handler rule applies.

## Events

Events represent something that already happened. They use pub/sub: zero, one, or many listeners can respond to the same event. Listeners run independently. If one throws, the rest still execute, and then `PublishAsync` throws an `EventListenerException` to the publisher (its `InnerException` is the first failure). A `[FireAndForget]` event's listeners run in the background, so their failures are logged instead.

```csharp
using Conduit.Messages;

public record TodoCompleted(Guid Id, string Title) : IEvent;
```

Listeners implement `IEventListener<TEvent>`:

```csharp
using Conduit.Handlers;

public class SendCompletionNotification(INotificationSender notifications) : IEventListener<TodoCompleted>
{
    public Task HandleAsync(TodoCompleted @event, CancellationToken cancellationToken)
        => notifications.SendAsync($"'{@event.Title}' is done", cancellationToken);
}

public class UpdateCompletionStats(TodoStats stats) : IEventListener<TodoCompleted>
{
    public Task HandleAsync(TodoCompleted @event, CancellationToken cancellationToken)
    {
        stats.RecordCompletion(@event.Id);
        return Task.CompletedTask;
    }
}
```

An event with zero listeners is valid. It has no effect locally, but unless it is `[LocalOnly]`, `PublishAsync`
also broadcasts it to the host's transports, so other services can listen to it ([Transports](transports/index.md)).

### Where an application's own listeners run

An application's own listeners run inline on the instance that publishes the event, never load-balanced across
its replicas. To spread heavy work across instances, publish a command routed to a worker endpoint instead. See
[Transports](transports/index.md) for how a broker delivery of the application's own event is handled.

## Dispatching

Inject `IMediator` and use the method that matches the message type:

```csharp
using Conduit;

public class TodoService(IMediator mediator)
{
    public async Task CompleteAsync(Guid id, CancellationToken cancellationToken)
    {
        // Queries use FetchAsync
        TodoDto todo = await mediator.FetchAsync(new GetTodoById(id), cancellationToken);

        // Commands use ExecuteAsync
        await mediator.ExecuteAsync(new DeleteTodo(id), cancellationToken);

        // Events use PublishAsync
        await mediator.PublishAsync(new TodoCompleted(id, todo.Title), cancellationToken);
    }
}
```

The method names are intentionally distinct. `ExecuteAsync` performs an action, `FetchAsync` reads data, `PublishAsync` broadcasts. There is no ambiguous `Send` method.

`IMediator` is registered as transient and holds no state, so it can be injected anywhere, a singleton such as a
`BackgroundService` included.

A message is dispatched by its exact type — by the full type name the wire carries, whether it is sent in process or over a transport. A record that derives from another message is a message of its own: it goes to its own handler, and a listener of the base event does not receive it. It does inherit its base's `[LocalOnly]` and `[FireAndForget]`. A message type cannot be generic ([CONDUIT007](diagnostics.md#conduit007--generic-message-types-are-not-supported)), and two assemblies cannot each declare one with the same full type name ([CONDUIT008](diagnostics.md#conduit008--message-type-declared-in-more-than-one-assembly)).

### Dispatch options

Each dispatch method (both forms of `ExecuteAsync`, `FetchAsync` and `PublishAsync`) has an overload that takes an
`Action<DispatchOptions>?` before the cancellation token.
`DispatchOptions.Tag(key, value)` attaches a property to that one dispatch:

```csharp
await mediator.ExecuteAsync(
    new DeleteTodo(id),
    options => options.Tag("Reason", "Duplicate").Tag("RequestedBy", userId),
    cancellationToken);
```

- The properties become the dispatch's `MessageContext.Current.Properties`, where a pipeline behavior or the
  handler reads them. The [audit trail](audit.md) records them with the dispatch.
- Every dispatch made inside it inherits them. A callback that tags anything replaces the inherited properties
  rather than adding to them; a `null` callback, or one that tags nothing, keeps them.
- Names are case-insensitive.
- They stay in this process. A transport does not send them to the endpoint a command is routed to, and the
  outbox keeps only the ones it is told to ([Carrying dispatch properties](outbox.md#carrying-dispatch-properties)).

## Validate()

`ICommand` and `IQuery<TResult>` have a default interface method called `Validate()`. Use it for structural precondition guards that are always invalid regardless of application state. The generated mediator calls `Validate()` innermost — after every behavior, immediately before the handler — so a behavior that refuses never runs the caller's input.

```csharp
using Conduit.Exceptions;
using Conduit.Messages;

public record TransferFunds(
    Guid FromAccountId,
    Guid ToAccountId,
    decimal Amount) : ICommand
{
    public void Validate()
    {
        if (FromAccountId == Guid.Empty)
        {
            throw new BadRequestException("Source account is required");
        }

        if (ToAccountId == Guid.Empty)
        {
            throw new BadRequestException("Destination account is required");
        }

        if (FromAccountId == ToAccountId)
        {
            throw new BadRequestException("Cannot transfer to the same account");
        }

        if (Amount <= 0)
        {
            throw new BadRequestException("Transfer amount must be positive");
        }
    }
}
```

These checks are structural. A transfer of zero dollars or to `Guid.Empty` is nonsensical regardless of what accounts exist. Throw `BadRequestException`, so a caller over a transport is answered 400 with the message: any exception that is not a `ConduitException` is answered as an unexpected failure, 500 with `"An unexpected error occurred."` ([Error Handling](error-handling.md)).

Validation that depends on application state belongs in the handler:

```csharp
using Conduit.Exceptions;
using Conduit.Handlers;

public class TransferFundsHandler(AccountStore accounts) : ICommandHandler<TransferFunds>
{
    public async Task HandleAsync(TransferFunds command, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(command.FromAccountId, cancellationToken)
            ?? throw new NotFoundException($"Account {command.FromAccountId} not found");

        if (account.Balance < command.Amount)
        {
            throw new ConflictException($"Balance {account.Balance} is less than {command.Amount}");
        }

        await accounts.TransferAsync(command.FromAccountId, command.ToAccountId, command.Amount, cancellationToken);
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
