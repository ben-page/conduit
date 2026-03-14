using AwesomeAssertions;
using Conduit.Dispatch;
using Conduit.Routing;
using Conduit.TestUtils;
using Conduit.Transport.SignalR.Tests.Support;
using Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR.Tests.Integration;

[TestClass]
public class EventBroadcastShould
{
    [TestMethod]
    public async Task SendEventToTransportOnPublish()
    {
        // Arrange
        var recordingTransport = new RecordingTransport();

        await using var host = TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddSingleton<IConduitTransport>(recordingTransport);
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("recorder", endpoint =>
                        endpoint.UseTransport(recordingTransport));
                    conduit.Route("Conduit.Transport.SignalR.Tests.Support").To("recorder");
                });
                builder.Services.AddConduitHandlers();
            });

        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Broadcast Test"));

        // Assert
        await spy.WaitForCountAsync(1);
        recordingTransport.SentEvents.Should().HaveCount(1);
        recordingTransport.SentEvents[0].Type.Should().Contain("ItemCreatedEvent");
    }

    [TestMethod]
    public async Task NotReBroadcastWireInboundEvent()
    {
        // Arrange
        var recordingTransport = new RecordingTransport();

        await using var host = TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddSingleton<IConduitTransport>(recordingTransport);
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddEndpoint("recorder", endpoint =>
                        endpoint.UseTransport(recordingTransport));
                    conduit.Route("Conduit.Transport.SignalR.Tests.Support").To("recorder");
                });
                builder.Services.AddConduitHandlers();
            });
        var wireMediator = host.GetRequiredService<IWireMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        var envelope = new DispatchEnvelope(
            "Conduit.Transport.SignalR.Tests.Support.ItemCreatedEvent",
            System.Text.Json.JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid(), Name = "Wire Event" }));

        // Act
        await wireMediator.HandleWireEventAsync(envelope, CancellationToken.None);

        // Assert
        await spy.WaitForCountAsync(1);
        recordingTransport.SentEvents.Should().BeEmpty();
    }
}

file class RecordingTransport : IConduitTransport
{
    public List<DispatchEnvelope> SentEvents { get; } = [];

    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => Task.FromResult<object?>(null);

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        this.SentEvents.Add(envelope);
        return Task.CompletedTask;
    }
}
