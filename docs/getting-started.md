# Getting Started

- [Overview](#overview)
- [Install packages](#install-packages)
- [Define a command and handler](#define-a-command-and-handler)
- [Wire up and dispatch](#wire-up-and-dispatch)
- [What the source generator produced](#what-the-source-generator-produced)

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
    <PackageReference Include="Conduit" />
    <PackageReference Include="Conduit.SourceGen" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <PackageReference Include="Conduit.Analysis" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
```

`Conduit.SourceGen` and `Conduit.Analysis` must be referenced as analyzers. The source generator runs at compile time to discover your message types and emit dispatch infrastructure.

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

The generated code is visible in your IDE under Analyzers > Conduit.SourceGen.

You now have in-process dispatch. The next sections show how to extend this to the browser and other services.

---

[Home](../README.md) | Next: [Core Concepts](core-concepts.md)
