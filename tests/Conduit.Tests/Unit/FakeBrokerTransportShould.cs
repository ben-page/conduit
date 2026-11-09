using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

[TestClass]
public class FakeBrokerTransportShould
{
    [TestMethod]
    public async Task RoundTripAnEventThroughSendAndInboundDispatch()
    {
        // Arrange
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit();
        });
        var options = host.GetRequiredService<ConduitOptions>();
        var transport = new FakeBrokerTransport(options, host.Services);
        transport.Start();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        var envelope = TestEnvelope.For(new ItemCreatedEvent(Guid.NewGuid(), "RoundTrip"));

        // Act
        await transport.SendEventAsync(envelope, CancellationToken.None);
        var published = transport.PublishedEvents.Single();
        await transport.DeliverAsync(new FakeBrokerMessage
        {
            Type = published.Type,
            Body = published.Body,
        });

        // Assert
        await spy.WaitForCountAsync(1);
        spy.Count.Should().Be(1);
    }

    [TestMethod]
    public async Task AcknowledgeAnEventTheHostRefuses()
    {
        // Arrange -- the type filter gates broker-delivered events too, and this one is not listed
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
                conduit.AddTypeFilter(filter => filter.AllowType(typeof(CreateItemCommand).FullName!)));
        });
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        transport.Start();
        var envelope = TestEnvelope.For(new ItemCreatedEvent(Guid.NewGuid(), "Refused"));
        var message = new FakeBrokerMessage { Type = envelope.Type, Body = new BinaryData(envelope.Payload.GetRawText()) };

        // Act
        await transport.DeliverAsync(message);

        // Assert -- a refusal is final: redelivering the message would be refused again
        message.Acknowledged.Should().BeTrue();
        message.Abandoned.Should().BeFalse();
        host.GetRequiredService<EventSpy<ItemCreatedEvent>>().Count.Should().Be(0);
    }
}
