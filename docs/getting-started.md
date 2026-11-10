# Getting Started

- [Overview](#overview)
- [Install packages](#install-packages)
- [Define a command and handler](#define-a-command-and-handler)
- [Wire up and dispatch](#wire-up-and-dispatch)
- [What the source generator produced](#what-the-source-generator-produced)
- [Namespaces](#namespaces)
- [Next](#next)

## Overview

Install Conduit and dispatch your first command in a single ASP.NET Core project.

## Install packages

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Conduit.NET" Version="1.0.0-preview.1" />
    <PackageReference Include="Conduit.NET.SourceGen" Version="1.0.0-preview.1" />
  </ItemGroup>
</Project>
```

`Conduit.NET` is the runtime. `Conduit.NET.SourceGen` is the source generator: it runs at compile time, discovers your message types and handlers, and emits the dispatch code. It is a development dependency, so NuGet adds it as an analyzer with nothing to configure, and nothing of it ships with your app. Reference it from the host project only, the one that calls `AddConduit()` (see [Modular Monolith](modular-monolith.md) for a solution with several projects).

A library that only declares messages, such as a contracts assembly shared with another service, references the `Conduit.NET.Contracts` package instead. It holds the message interfaces, attributes and exceptions and has no package dependencies of its own. `Conduit.NET` depends on it, so the host gets it too.

Every package id starts `Conduit.NET`; the assemblies and namespaces are `Conduit`, `Conduit.Messages` and so on.

## Define a command and handler

```csharp
using Conduit.Handlers;
using Conduit.Messages;

public record CreateTodoCommand(string Title) : ICommand<Guid>;

public class CreateTodoHandler : ICommandHandler<CreateTodoCommand, Guid>
{
    public Task<Guid> HandleAsync(CreateTodoCommand command, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        Console.WriteLine($"Created todo '{command.Title}' with id {id}");
        return Task.FromResult(id);
    }
}
```

`ICommand<Guid>` declares a command that returns a `Guid`. The source generator finds `CreateTodoCommand` and `CreateTodoHandler` at compile time. No registration attributes or assembly scanning required.

## Wire up and dispatch

```csharp
using Conduit;
using Conduit.Generated;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddConduit();

var app = builder.Build();

app.MapGet("/", async (IMediator mediator) =>
{
    var id = await mediator.ExecuteAsync(new CreateTodoCommand("Buy milk"));
    return Results.Ok(new { id });
});

app.Run();
```

`AddConduit()` is source-generated. It registers all discovered handlers and the generated `ConduitMediator` with DI. If the source generator can see your type, it gets wired up.

`IMediator` is the dispatch interface your application code uses. Inject it anywhere and call `ExecuteAsync` for commands, `FetchAsync` for queries, or `PublishAsync` for events.

## What the source generator produced

At compile time, the source generator emitted:

- A `ConduitMediator` that dispatches `CreateTodoCommand` directly to `CreateTodoHandler` via a compiled type switch. No reflection, no `Dictionary<Type, ...>` lookups.
- An `AddConduit()` extension method that registers everything in DI.

The generated code is visible in your IDE under Analyzers > Conduit.SourceGen (the generator's assembly name).

## Namespaces

| Namespace | Package | Holds |
|---|---|---|
| `Conduit` | `Conduit.NET` | `IMediator`, the interface application code dispatches through |
| `Conduit.Messages` | `Conduit.NET.Contracts` | `ICommand`, `ICommand<TResult>`, `IQuery<TResult>`, `IEvent` |
| `Conduit.Handlers` | `Conduit.NET` | `ICommandHandler<TCommand>`, `ICommandHandler<TCommand, TResult>`, `IQueryHandler<TQuery, TResult>`, `IEventListener<TEvent>` |
| `Conduit.Attributes` | `Conduit.NET.Contracts` | `[FireAndForget]`, `[LocalOnly]`, `[ClientVisible]`, `[RemoteContracts]`, the audit attributes |
| `Conduit.Exceptions` | `Conduit.NET.Contracts` | `NotFoundException`, `ConflictException` and the other refusals a handler throws ([Error Handling](error-handling.md)) |
| `Conduit.Generated` | generated into your project | `AddConduit()` and the generated mediator |

## Next

You now have in-process dispatch. [Core Concepts](core-concepts.md) covers commands, queries, events and the mediator in full. [Transports](transports/index.md) takes the same messages to a browser and to other services.

---

[Home](../README.md) | Next: [Core Concepts](core-concepts.md)
