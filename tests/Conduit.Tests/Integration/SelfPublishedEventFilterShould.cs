using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

/// <summary>
/// Guards the inbound self-published event filter: an application's own broadcast copy comes
/// straight back through its per-app event queue (bound to every discovered event type), and
/// must be dropped because the local listeners already ran inline in PublishAsync.
/// </summary>
[TestClass]
public sealed class SelfPublishedEventFilterShould
{
    private static TestFixture StartHost(string? applicationName)
    {
        var observations = new ObservationCollector();
        var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddSingleton<EventSpy<ImportDataCommand>>();
            builder.Services.AddSingleton<IConduitObserver>(observations);
            builder.Services.AddConduit();
        });
        var options = host.GetRequiredService<ConduitOptions>();
        options.ApplicationName = applicationName;
        var transport = new FakeBrokerTransport(options, host.Services);
        transport.Start();
        return new TestFixture(
            host, transport, host.GetRequiredService<EventSpy<ItemCreatedEvent>>(), observations);
    }

    private static FakeBrokerMessage InboundEvent(string? sourceApp)
    {
        var envelope = TestEnvelope.For(new ItemCreatedEvent(Guid.NewGuid(), "Inbound"));
        var message = new FakeBrokerMessage
        {
            Type = envelope.Type,
            Body = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
                envelope.Payload, ConduitRuntime.JsonOptions)),
        };

        if (sourceApp is not null)
        {
            message.Properties[ConduitHeaders.SourceApp] = sourceApp;
        }

        return message;
    }

    [TestMethod]
    public async Task SkipAnEventThisApplicationPublished()
    {
        // Arrange
        var fixture = StartHost("OrderService");
        await using var host = fixture.Host;
        var message = InboundEvent("OrderService");

        // Act
        await fixture.Transport.DeliverAsync(message);

        // Assert — listeners already ran inline at publish time
        fixture.Spy.Count.Should().Be(0);
        message.Acknowledged.Should().BeTrue("abandoning would redeliver forever");
        message.Abandoned.Should().BeFalse();
        fixture.Observations.OfType<SelfPublishedEventSkipped>().Should().ContainSingle()
            .Which.MessageType.Should().Be(typeof(ItemCreatedEvent).FullName);
    }

    [TestMethod]
    public async Task DispatchAnEventFromAnotherApplication()
    {
        // Arrange
        var fixture = StartHost("OrderService");
        await using var host = fixture.Host;

        // Act
        await fixture.Transport.DeliverAsync(InboundEvent("InventoryService"));

        // Assert
        await fixture.Spy.WaitForCountAsync(1);
        fixture.Spy.Count.Should().Be(1);
        fixture.Observations.OfType<SelfPublishedEventSkipped>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task DispatchAnEventWithNoSourceStamp()
    {
        // Arrange — an in-flight message published before the upgrade
        var fixture = StartHost("OrderService");
        await using var host = fixture.Host;

        // Act
        await fixture.Transport.DeliverAsync(InboundEvent(sourceApp: null));

        // Assert
        await fixture.Spy.WaitForCountAsync(1);
        fixture.Spy.Count.Should().Be(1);
    }

    [TestMethod]
    public async Task DispatchEveryEventWhenTheApplicationIsUnnamed()
    {
        // Arrange — a null app name must never compare equal to a missing header
        var fixture = StartHost(applicationName: null);
        await using var host = fixture.Host;

        // Act
        await fixture.Transport.DeliverAsync(InboundEvent(sourceApp: null));

        // Assert
        await fixture.Spy.WaitForCountAsync(1);
        fixture.Spy.Count.Should().Be(1);
    }

    [TestMethod]
    public async Task DispatchEveryEventWhenTheApplicationNameIsEmpty()
    {
        // Arrange — an empty app name is as unnamed as a null one. A producer that stamped an
        // empty name must not match it, or this host would discard every inbound event.
        var fixture = StartHost(applicationName: string.Empty);
        await using var host = fixture.Host;

        // Act
        await fixture.Transport.DeliverAsync(InboundEvent(sourceApp: string.Empty));

        // Assert
        await fixture.Spy.WaitForCountAsync(1);
        fixture.Spy.Count.Should().Be(1);
        fixture.Observations.OfType<SelfPublishedEventSkipped>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task NotFilterFireAndForgetCommandsFromThisApplication()
    {
        // Arrange — a routed [FireAndForget] command is sent via SendEventAsync, so it arrives
        // with IsRequest == false and this application's own stamp, exactly like a self-published
        // event. It is not an event, and dropping it would lose the command silently.
        var fixture = StartHost("OrderService");
        await using var host = fixture.Host;
        var commandSpy = host.GetRequiredService<EventSpy<ImportDataCommand>>();
        var envelope = TestEnvelope.ForRequest(new ImportDataCommand("/imports/orders.csv"));
        var message = new FakeBrokerMessage
        {
            Type = envelope.Type,
            Body = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
                envelope.Payload, ConduitRuntime.JsonOptions)),
        };
        message.Properties[ConduitHeaders.SourceApp] = "OrderService";

        // Act
        await fixture.Transport.DeliverAsync(message);

        // Assert — the handler ran, so the message reached dispatch rather than the filter
        await commandSpy.WaitForCountAsync(1);
        commandSpy.Count.Should().Be(1);
        fixture.Observations.OfType<SelfPublishedEventSkipped>().Should().BeEmpty();
        message.Acknowledged.Should().BeTrue();
    }

    [TestMethod]
    public async Task NotFilterRequestsFromThisApplication()
    {
        // Arrange — a command routed to an endpoint may legitimately originate here, so the
        // filter must never look at a request, even one stamped with this application's name.
        var fixture = StartHost("OrderService");
        await using var host = fixture.Host;
        var envelope = TestEnvelope.ForRequest(new EchoCommand("RoutedHome"));
        var request = new FakeBrokerMessage
        {
            Type = envelope.Type,
            Body = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
                envelope.Payload, ConduitRuntime.JsonOptions)),
            IsRequest = true,
        };
        request.Properties[ConduitHeaders.SourceApp] = "OrderService";

        // Act
        await fixture.Transport.DeliverAsync(request);

        // Assert — the filter did not drop it: no skip observation was emitted, and the message
        // travelled the request path far enough to produce a reply carrying the handler's result.
        // The filter's early return sends no reply, so a reply can only come from dispatch.
        fixture.Observations.OfType<SelfPublishedEventSkipped>().Should().BeEmpty();
        fixture.Transport.Replies.Should().ContainSingle();
        fixture.Transport.Replies[0].ToString().Should().Contain("RoutedHome");
    }

    private sealed record TestFixture(
        TestWebHost Host,
        FakeBrokerTransport Transport,
        EventSpy<ItemCreatedEvent> Spy,
        ObservationCollector Observations);
}
