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
