using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Middleware;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
public class OutboundInterceptionShould
{
    [TestMethod]
    public async Task CapturePublishedEventWhenInterceptorRegistered()
    {
        // Arrange
        var spyTransport = new SpyTransport();
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddScoped<IOutboundInterceptor, SpyOutboundInterceptor>();
            builder.Services.AddSingleton<IConduitTransport>(spyTransport);
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();
        var interceptor = (SpyOutboundInterceptor)host.GetRequiredService<IOutboundInterceptor>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Intercepted"));

        // Assert -- interceptor captured the event, transport did NOT
        interceptor.Captured.Should().ContainSingle();
        interceptor.Captured[0].Type.Should().Contain("ItemCreatedEvent");
        spyTransport.SentEvents.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SendDirectlyWhenNoInterceptorRegistered()
    {
        // Arrange
        var spyTransport = new SpyTransport();
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddSingleton<IConduitTransport>(spyTransport);
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Direct"));

        // Assert -- transport received the event directly
        spyTransport.SentEvents.Should().ContainSingle();
        spyTransport.SentEvents[0].Type.Should().Contain("ItemCreatedEvent");
    }

    [TestMethod]
    public async Task CaptureFireAndForgetVoidCommandWhenInterceptorRegistered()
    {
        // Arrange
        var spyTransport = new SpyTransport();
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddSingleton<EventSpy<ImportDataCommand>>();
            builder.Services.AddScoped<IOutboundInterceptor, SpyOutboundInterceptor>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddEndpoint("remote", endpoint =>
                {
                    endpoint.UseTransport(spyTransport);
                    endpoint.RouteNamespace<ImportDataCommand>();
                });
            });
        });
        var mediator = host.GetRequiredService<IMediator>();
        var interceptor = (SpyOutboundInterceptor)host.GetRequiredService<IOutboundInterceptor>();

        // Act — ImportDataCommand is [FireAndForget], so it should be intercepted
        await mediator.ExecuteAsync(new ImportDataCommand("/data/file.csv"));

        // Assert -- interceptor captured the F&F command, transport did NOT receive it
        interceptor.Captured.Should().ContainSingle();
        interceptor.Captured[0].Type.Should().Contain("ImportDataCommand");
        spyTransport.SentEvents.Should().BeEmpty();
    }

    [TestMethod]
    public async Task NotInterceptNonFireAndForgetVoidCommand()
    {
        // Arrange
        var spyTransport = new SpyTransport();
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddScoped<IOutboundInterceptor, SpyOutboundInterceptor>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddEndpoint("remote", endpoint =>
                {
                    endpoint.UseTransport(spyTransport);
                    endpoint.RouteNamespace<DeleteItemCommand>();
                });
            });
        });
        var mediator = host.GetRequiredService<IMediator>();
        var interceptor = (SpyOutboundInterceptor)host.GetRequiredService<IOutboundInterceptor>();

        // Act — DeleteItemCommand is NOT [FireAndForget], so it should NOT be intercepted
        await mediator.ExecuteAsync(new DeleteItemCommand(Guid.NewGuid()));

        // Assert -- interceptor was NOT called, transport received the command directly
        interceptor.Captured.Should().BeEmpty();
        spyTransport.SentRequests.Should().ContainSingle();
    }

    [TestMethod]
    public async Task NotInterceptLocalOnlyEvents()
    {
        // Arrange
        var spyTransport = new SpyTransport();
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddScoped<IOutboundInterceptor, SpyOutboundInterceptor>();
            builder.Services.AddSingleton<IConduitTransport>(spyTransport);
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();
        var interceptor = (SpyOutboundInterceptor)host.GetRequiredService<IOutboundInterceptor>();

        // Act
        await mediator.PublishAsync(new LocalOnlyEvent("Secret"));

        // Assert -- LocalOnly events skip broadcast entirely, interceptor NOT called
        interceptor.Captured.Should().BeEmpty();
        spyTransport.SentEvents.Should().BeEmpty();
    }
}
