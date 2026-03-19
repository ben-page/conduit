using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
public class EventBroadcastShould
{
    [TestMethod]
    public async Task BroadcastEventToRegisteredTransport()
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
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Test"));

        // Assert — dispatched locally AND broadcast to transport
        await spy.WaitForCountAsync(1);
        spyTransport.SentEvents.Should().HaveCount(1);
        spyTransport.SentEvents[0].Type.Should().Contain("ItemCreatedEvent");
    }

    [TestMethod]
    public async Task BroadcastEventToMultipleTransports()
    {
        // Arrange
        var spyA = new SpyTransport();
        var spyB = new SpyTransport();
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddSingleton<IConduitTransport>(spyA);
            builder.Services.AddSingleton<IConduitTransport>(spyB);
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Multi"));

        // Assert — broadcast to both transports
        await spy.WaitForCountAsync(1);
        spyA.SentEvents.Should().HaveCount(1);
        spyB.SentEvents.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task NotBroadcastWhenNoTransportsRegistered()
    {
        // Arrange — no IConduitTransport in DI
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Local"));

        // Assert — local listeners fire, no errors
        await spy.WaitForCountAsync(1);
    }

    [TestMethod]
    public async Task NotSendCommandsToBroadcastTransports()
    {
        // Arrange — DI transport should only receive events, not commands
        var broadcastTransport = new SpyTransport();
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddSingleton<IConduitTransport>(broadcastTransport);
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();

        // Act — dispatch a command (no route configured, so handled locally)
        await mediator.ExecuteAsync(new EchoCommand("Test"));

        // Assert — broadcast transport did NOT receive the command
        broadcastTransport.SentRequests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task NotBroadcastLocalOnlyEvent()
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
        await mediator.PublishAsync(new LocalOnlyEvent("Secret"));

        // Assert — NOT broadcast to transport
        spyTransport.SentEvents.Should().BeEmpty();
    }
}
