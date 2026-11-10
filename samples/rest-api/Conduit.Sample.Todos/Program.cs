using Conduit.Generated;
using Conduit.Sample.Todos;
using Conduit.Sample.Todos.Commands;
using Conduit.Sample.Todos.Queries;

namespace Conduit.Sample.Todos;

/// <summary>
/// REST API sample demonstrating Conduit's RESTful endpoint mapping.
/// Each message type maps to a conventional HTTP route — no envelope dispatch needed.
/// Run and open https://localhost:49877/swagger to interact with the API.
/// </summary>
public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddSingleton<TodoStore>();
        builder.Services.AddConduit();
        builder.Services.AddOpenApi();

        var app = builder.Build();

        app.MapOpenApi();
        app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Todos API"));

        // RESTful endpoints — each maps a Conduit message type to a route
        app.MapConduitGet<GetTodosQuery>("/api/todos")
            .WithTags("Todos");

        app.MapConduitGet<GetTodoByIdQuery>("/api/todos/{id}")
            .WithTags("Todos")
            .ProducesProblem(404);

        app.MapConduitPost<CreateTodoCommand>("/api/todos")
            .WithTags("Todos");

        app.MapConduitPut<UpdateTodoCommand>("/api/todos/{id}")
            .WithTags("Todos")
            .ProducesProblem(404);

        app.MapConduitDelete<DeleteTodoCommand>("/api/todos/{id}")
            .WithTags("Todos");

        app.Run();
    }
}
