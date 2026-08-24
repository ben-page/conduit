using System.Diagnostics;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class BrokerOutboundHeadersShould : IDisposable
{
    private ActivityListener? listener;

    [TestInitialize]
    public void Setup()
    {
        // A traceparent header is only emitted onto a published message when Activity.Current is
        // non-null at publish time, which requires a listener registered against Conduit's
        // ActivitySource — without one, ConduitTelemetry.StartProducerActivity returns null.
        this.listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Conduit",
            Sample = SampleAllData,
        };
        ActivitySource.AddActivityListener(this.listener);
    }

    private static ActivitySamplingResult SampleAllData(
        ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded;

    [TestCleanup]
    public void Cleanup()
    {
        this.listener?.Dispose();
        this.listener = null;
    }

    public void Dispose()
    {
        this.listener?.Dispose();
    }

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

        // Assert — trace context is call-affine too, not just message id: no two publishes
        // share a traceparent. This is the value the removed [ThreadStatic] state actually leaked.
        var traceParents = transport.PublishedEvents
            .Select(message => message.Properties["traceparent"])
            .ToList();
        traceParents.Should().HaveCount(50);
        traceParents.Should().OnlyHaveUniqueItems();
    }

    [TestMethod]
    public async Task StampThePublishingApplicationOnOutboundEvents()
    {
        // Arrange
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit();
        });
        var options = host.GetRequiredService<ConduitOptions>();
        options.ApplicationName = "OrderService";
        var transport = new FakeBrokerTransport(options, host.Services);
        transport.Start();

        // Act
        await transport.SendEventAsync(
            TestEnvelope.For(new ItemCreatedEvent(Guid.NewGuid(), "Stamped")),
            CancellationToken.None);

        // Assert
        transport.PublishedEvents.Single()
            .Properties[ConduitHeaders.SourceApp].Should().Be("OrderService");
    }

    [TestMethod]
    public async Task OmitTheStampWhenTheApplicationNameIsEmpty()
    {
        // Arrange
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit();
        });
        var options = host.GetRequiredService<ConduitOptions>();
        options.ApplicationName = string.Empty;
        var transport = new FakeBrokerTransport(options, host.Services);
        transport.Start();

        // Act
        await transport.SendEventAsync(
            TestEnvelope.For(new ItemCreatedEvent(Guid.NewGuid(), "Unstamped")),
            CancellationToken.None);

        // Assert — an empty name is as unnamed as a null one. Stamping it would make every
        // empty-named host match its own header on the inbound filter and drop every event.
        transport.PublishedEvents.Single()
            .Properties.Should().NotContainKey(ConduitHeaders.SourceApp);
    }

    [TestMethod]
    public async Task OmitTheStampWhenTheApplicationIsUnnamed()
    {
        // Arrange
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit();
        });
        var options = host.GetRequiredService<ConduitOptions>();
        options.ApplicationName = null;
        var transport = new FakeBrokerTransport(options, host.Services);
        transport.Start();

        // Act
        await transport.SendEventAsync(
            TestEnvelope.For(new ItemCreatedEvent(Guid.NewGuid(), "Unstamped")),
            CancellationToken.None);

        // Assert — an unnamed app must not stamp, or the inbound filter would match
        // a missing header against a null name and discard every inbound event.
        transport.PublishedEvents.Single()
            .Properties.Should().NotContainKey(ConduitHeaders.SourceApp);
    }
}
