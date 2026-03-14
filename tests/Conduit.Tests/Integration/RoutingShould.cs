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
    public async Task DispatchEventLocallyAndToMatchingTransports()
    {
        // Arrange — route matches the event namespace
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
                    conduit.Route("Conduit.Tests.Support").To("remote");
                });
            });
        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Both"));

        // Assert — dispatched locally AND to transport
        await spy.WaitForCountAsync(1);
        spyTransport.SentEvents.Should().HaveCount(1);
        spyTransport.SentEvents[0].Type.Should().Contain("ItemCreatedEvent");
    }

    [TestMethod]
    public async Task NotRouteEventToUnmatchedTransport()
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
                    conduit.Route("Remote.Events").To("remote");
                });
            });
        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Local Only"));

        // Assert — dispatched locally, but NOT to the unmatched transport
        await spy.WaitForCountAsync(1);
        spyTransport.SentEvents.Should().BeEmpty();
    }

    [TestMethod]
    public async Task RouteEventToMultipleMatchingTransports()
    {
        // Arrange — two endpoints with routes matching the event namespace
        var spyTransportA = new SpyTransport();
        var spyTransportB = new SpyTransport();
        await using var host = TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("endpointA", endpoint =>
                        endpoint.UseTransport(spyTransportA));
                    conduit.Route("Conduit.Tests.Support").To("endpointA");

                    conduit.AddEndpoint("endpointB", endpoint =>
                        endpoint.UseTransport(spyTransportB));
                    conduit.Route("Conduit.Tests").To("endpointB");
                });
            });
        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Multi"));

        // Assert — dispatched locally AND to both matching transports
        await spy.WaitForCountAsync(1);
        spyTransportA.SentEvents.Should().HaveCount(1);
        spyTransportB.SentEvents.Should().HaveCount(1);
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
