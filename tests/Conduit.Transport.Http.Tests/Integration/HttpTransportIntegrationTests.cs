using AwesomeAssertions;
using Conduit.Bridge.Http;
using Conduit.TestUtils;
using Conduit.Transport.Http.Tests.TestApp;
using Conduit.Transport.SignalR;
using Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.Http.Tests.Integration;

[TestClass]
public class HttpTransportIntegrationTests
{
    private static TestHost CreateHandlerHost()
    {
        return TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
                builder.Services.AddConduitHandlers();
            },
            app =>
            {
                app.MapConduitBridge();
                app.MapHub<ConduitHub>("/hub");
            });
    }

    private static TestHost CreateCallerHost(string remoteAddress)
    {
        return TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("remote", endpoint =>
                        endpoint.UseTransport(new HttpTransport(
                            new HttpClient { BaseAddress = new Uri(remoteAddress) })));
                    conduit.Route("Conduit.Transport.Http.Tests.TestApp").To("remote");
                });
                builder.Services.AddConduitSignalR();
                builder.Services.AddConduitHandlers();
            },
            app => app.MapHub<ConduitHub>("/hub"));
    }

    [TestMethod]
    public async Task CommandWithResult_RoundTrips_ThroughHttp()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var id = await mediator.ExecuteAsync(new CreateItemCommand("HTTP Test"));

        // Assert
        id.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task VoidCommand_RoundTrips_ThroughHttp()
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
    public async Task Query_RoundTrips_ThroughHttp()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        handlerHost.GetRequiredService<ItemStore>().Add(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "Query Test");

        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.FetchAsync(new GetItemQuery(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")));

        // Assert
        result.Should().Be("Query Test");
    }

    [TestMethod]
    public async Task NotFoundException_PropagatesAsHttpRequestException()
    {
        // Arrange
        await using var handlerHost = CreateHandlerHost();
        await using var callerHost = CreateCallerHost(handlerHost.Address);
        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.FetchAsync(new GetItemQuery(Guid.NewGuid()));

        // Assert
        await action.Should().ThrowAsync<HttpRequestException>();
    }
}
