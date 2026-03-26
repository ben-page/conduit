using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Generated;
using Conduit.Transport.SignalR.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR.Tests.Integration;

[TestClass]
public class SignalRTransportShould
{
    private static TestWebHost CreateHandlerHost()
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
            },
            app => app.MapHub<ConduitHub>("/conduit"));
    }

    private static TestWebHost CreateCallerHost(string remoteAddress)
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("remote", endpoint =>
                    {
                        endpoint.UseSignalR(new Uri($"{remoteAddress}/conduit"));
                        endpoint.RouteNamespace<CreateItemCommand>();
                    });
                });
                builder.Services.AddConduitSignalR();
            },
            app => app.MapHub<ConduitHub>("/conduit"));
    }

    [TestMethod]
    public async Task RoundTripCommandWithResult()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var id = await mediator.ExecuteAsync(new CreateItemCommand("SignalR Test"));

        // Assert — verify the item was created on the HANDLER host, not the caller
        id.Should().NotBeEmpty();
        handlerHost.GetRequiredService<ItemStore>().Get(id).Should().Be("SignalR Test");
    }

    [TestMethod]
    public async Task RoundTripVoidCommand()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        var store = handlerHost.GetRequiredService<ItemStore>();
        var id = Guid.NewGuid();
        store.Add(id, "To Delete");
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new DeleteItemCommand(id));

        // Assert
        store.Get(id).Should().BeNull();
    }

    [TestMethod]
    public async Task RoundTripQuery()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        handlerHost.GetRequiredService<ItemStore>().Add(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "Query Test");

        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.FetchAsync(new GetItemQuery(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")));

        // Assert
        result.Should().Be("Query Test");
    }

    [TestMethod]
    public async Task PropagateNotFoundException()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.FetchAsync(new GetItemQuery(Guid.NewGuid()));

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*NotFound*");
    }

    [TestMethod]
    public async Task PropagateGenericExceptionAsInternalError()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.ExecuteAsync(new ExplodingCommand());

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*InternalError*");
    }
}
