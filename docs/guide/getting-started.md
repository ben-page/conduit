# Getting Started

A minimal single-project example: define a command, handle it, dispatch it.

## Project setup

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="path\to\Conduit.csproj" />
    <ProjectReference Include="path\to\Conduit.SourceGen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <ProjectReference Include="path\to\Conduit.Analysis.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
```

Both `Conduit.SourceGen` and `Conduit.Analysis` must be referenced as analyzers. The source generator runs at compile time to discover your message types and emit the dispatch infrastructure.

When Conduit ships as a NuGet package, this will be a single `<PackageReference>`.

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

`ICommand<Guid>` declares a command that returns a `Guid`. The source generator finds `CreateTodoCommand` and its handler automatically — no registration attributes, no assembly scanning.

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

`AddConduit()` is source-generated. It registers all discovered handlers and the mediator implementation with DI. No manual registration needed — if the source generator can see your type, it gets wired up.

`IMediator` is the only interface your application code needs. Inject it anywhere and call `ExecuteAsync` for commands, `FetchAsync` for queries, or `PublishAsync` for events.

## What happened

The source generator found `CreateTodoCommand` and `CreateTodoHandler` at compile time and generated:

- A `ConduitMediator` class implementing `IMediator` that dispatches `CreateTodoCommand` to `CreateTodoHandler`
- An `AddConduit()` extension method on `IServiceCollection` that registers everything in DI

No reflection. No assembly scanning at startup. The generated code is plain C# you can inspect in your IDE under Analyzers.

You now have in-process dispatch. The next sections show how to extend this to the browser and other services.

Next: [Core Concepts](core-concepts.md)
