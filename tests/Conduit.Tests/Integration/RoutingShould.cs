using AwesomeAssertions;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
public class RoutingShould
{
    [TestMethod]
    public async Task DispatchCommandLocallyWhenNoRouteMatches()
    {
        // Arrange — route configured for a different namespace
        var spyTransport = new SpyTransport();
        await using var host = TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("remote", endpoint =>
                        endpoint.UseTransport(spyTransport));
                    conduit.Route("Remote.Commands").To("remote");
                });
            });
        var mediator = host.GetRequiredService<IMediator>();
        var store = host.GetRequiredService<ItemStore>();

        // Act
        var id = await mediator.ExecuteAsync(new CreateItemCommand("Local"));

        // Assert — handled locally, transport not called
        id.Should().NotBeEmpty();
        store.Get(id).Should().Be("Local");
        spyTransport.SentRequests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task RouteCommandToFirstMatchingTransport()
    {
        // Arrange — route matches the test type namespace
        var spyTransport = new SpyTransport { RequestResult = Guid.NewGuid() };
        await using var host = TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("remote", endpoint =>
                        endpoint.UseTransport(spyTransport));
                    conduit.Route("Conduit.Tests.Support").To("remote");
                });
            });
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new CreateItemCommand("Routed"));

        // Assert — command sent to transport, NOT handled locally
        spyTransport.SentRequests.Should().HaveCount(1);
        spyTransport.SentRequests[0].Type.Should().Contain("CreateItemCommand");
    }

    [TestMethod]
    public async Task RouteCommandUsingGenericTypeOverload()
    {
        // Arrange — use Route<T>() which routes by the type's namespace
        var spyTransport = new SpyTransport { RequestResult = Guid.NewGuid() };
        await using var host = TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("remote", endpoint =>
                        endpoint.UseTransport(spyTransport));
                    conduit.Route<CreateItemCommand>().To("remote");
                });
            });
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new CreateItemCommand("Generic Route"));

        // Assert — routed via generic type's namespace
        spyTransport.SentRequests.Should().HaveCount(1);
    }
}
