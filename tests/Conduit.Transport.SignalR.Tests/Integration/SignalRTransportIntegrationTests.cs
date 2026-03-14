using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.SignalR.Tests.TestApp;
using global::Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR.Tests.Integration;

[TestClass]
public class SignalRTransportIntegrationTests
{
    private static TestHost CreateHandlerHost()
    {
        return TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
                builder.Services.AddConduitHandlers();
            },
            app => app.MapHub<ConduitHub>("/hub"));
    }

    private static TestHost CreateCallerHost(string remoteAddress)
    {
        return TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("remote", endpoint =>
                        endpoint.UseSignalR(new Uri($"{remoteAddress}/hub")));
                    conduit.Route("Conduit.Transport.SignalR.Tests.TestApp").To("remote");
                });
                builder.Services.AddConduitSignalR();
                builder.Services.AddConduitHandlers();
            },
            app => app.MapHub<ConduitHub>("/hub"));
    }

    [TestMethod]
    public async Task CommandWithResult_RoundTrips_ThroughSignalR()
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
    public async Task VoidCommand_RoundTrips_ThroughSignalR()
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
    public async Task Query_RoundTrips_ThroughSignalR()
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
    public async Task NotFoundException_Propagates_ThroughSignalR()
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
    public async Task GenericException_Propagates_AsInternalError()
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
