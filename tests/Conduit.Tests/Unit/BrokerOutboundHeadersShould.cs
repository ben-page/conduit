using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

[TestClass]
public class BrokerOutboundHeadersShould
{
    [TestMethod]
    public async Task CarryTheCorrelationIdsOfTheDispatchThatPublished()
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
        var envelope = TestEnvelope.For(new ItemCreatedEvent(Guid.NewGuid(), "Correlated"));

        // Act
        MessageContext.StartDispatch();
        var expectedMessageId = MessageContext.Current!.MessageId;
        await transport.SendEventAsync(envelope, CancellationToken.None);

        // Assert
        var published = transport.PublishedEvents.Single();
        published.Properties[ConduitHeaders.MessageId].Should().Be(expectedMessageId);
        published.Properties[ConduitHeaders.ConversationId]
            .Should().Be(MessageContext.Current!.ConversationId);
    }

    [TestMethod]
    public async Task NotBleedHeadersBetweenConcurrentPublishes()
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

        // Act — 50 concurrent publishes, each under its own MessageContext
        var sends = Enumerable.Range(0, 50).Select(index => Task.Run(async () =>
        {
            MessageContext.StartDispatch();
            var expectedMessageId = MessageContext.Current!.MessageId;
            await transport.SendEventAsync(
                TestEnvelope.For(new ItemCreatedEvent(Guid.NewGuid(), $"Concurrent-{index}")),
                CancellationToken.None);
            return expectedMessageId;
        }));
        var expectedIds = await Task.WhenAll(sends);

        // Assert — every published message carries a distinct id, and exactly the expected set
        var publishedIds = transport.PublishedEvents
            .Select(message => message.Properties[ConduitHeaders.MessageId])
            .ToList();
        publishedIds.Should().HaveCount(50);
        publishedIds.Should().OnlyHaveUniqueItems();
        publishedIds.Should().BeEquivalentTo(expectedIds);
    }
}
