using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.SignalR;
using Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Conduit.Tests.Support;

namespace Conduit.Tests.Integration;

[TestClass]
public class MediatorShould
{
    private static TestHost CreateTestHost(Action<IServiceCollection>? configure = null)
    {
        return TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
                builder.Services.AddConduitHandlers();
                configure?.Invoke(builder.Services);
            },
            app => app.MapHub<ConduitHub>("/hub"));
    }

    [TestMethod]
    public async Task ExecuteCommandWithResult()
    {
        // Arrange
        await using var host = CreateTestHost();
        var mediator = host.GetRequiredService<IMediator>();
        var store = host.GetRequiredService<ItemStore>();

        // Act
        var id = await mediator.ExecuteAsync(new CreateItemCommand("Test"));

        // Assert
        id.Should().NotBeEmpty();
        store.Get(id).Should().Be("Test");
    }

    [TestMethod]
    public async Task ExecuteVoidCommand()
    {
        // Arrange
        await using var host = CreateTestHost();
        var mediator = host.GetRequiredService<IMediator>();
        var store = host.GetRequiredService<ItemStore>();
        var id = Guid.NewGuid();
        store.Add(id, "To Delete");

        // Act
        await mediator.ExecuteAsync(new DeleteItemCommand(id));

        // Assert
        store.Get(id).Should().BeNull();
    }

    [TestMethod]
    public async Task ExecuteQuery()
    {
        // Arrange
        await using var host = CreateTestHost();
        var mediator = host.GetRequiredService<IMediator>();
        var store = host.GetRequiredService<ItemStore>();
        var id = Guid.NewGuid();
        store.Add(id, "Query Test");

        // Act
        var result = await mediator.FetchAsync(new GetItemQuery(id));

        // Assert
        result.Should().Be("Query Test");
    }

    [TestMethod]
    public async Task DeliverEventToListener()
    {
        // Arrange
        await using var host = CreateTestHost();
        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Test"));

        // Assert
        await spy.WaitForCountAsync(1);
    }

    [TestMethod]
    public async Task DispatchUnroutedMessageLocally()
    {
        // Arrange — configure a route for "Remote.Commands" namespace,
        // but our test types are in "Conduit.Tests.TestApp" so they won't match
        await using var host = TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("remote", endpoint =>
                        endpoint.UseTransport(new StubTransport()));
                    conduit.Route("Remote.Commands").To("remote");
                });
                builder.Services.AddConduitSignalR();
                builder.Services.AddConduitHandlers();
            },
            app => app.MapHub<ConduitHub>("/hub"));
        var mediator = host.GetRequiredService<IMediator>();
        var store = host.GetRequiredService<ItemStore>();

        // Act
        var id = await mediator.ExecuteAsync(new CreateItemCommand("Local"));

        // Assert
        id.Should().NotBeEmpty();
        store.Get(id).Should().Be("Local");
    }
}

file class StubTransport : Routing.IConduitTransport
{
    public Task<object?> SendRequestAsync(Dispatch.DispatchEnvelope envelope, CancellationToken cancellationToken)
        => Task.FromResult<object?>(null);

    public Task SendEventAsync(Dispatch.DispatchEnvelope envelope, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
