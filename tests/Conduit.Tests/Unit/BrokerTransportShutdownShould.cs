using System.Diagnostics;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

/// <summary>
/// The base class's stop sequence, without a broker: stop receiving, drain the handlers still running
/// (bounded by the shutdown token), and only then close what they acknowledge on.
/// </summary>
[TestClass]
public class BrokerTransportShutdownShould
{
    [TestMethod]
    public async Task AcknowledgeAnInFlightMessageBeforeClosing()
    {
        // Arrange
        await using var host = StartHost();
        var gate = host.GetRequiredService<DispatchGate>();
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        transport.Start();
        var message = EventMessage("in-flight");
        var delivery = transport.DeliverAsync(message);
        await gate.Entered;

        // Act
        var stopping = transport.StopAsync(CancellationToken.None);
        await Task.Delay(200);
        var stoppedWhileRunning = stopping.IsCompleted;
        gate.Open();
        await stopping;

        // Assert -- stop waited for the handler, and closed only after it acknowledged
        stoppedWhileRunning.Should().BeFalse();
        delivery.IsCompleted.Should().BeTrue();
        message.Acknowledged.Should().BeTrue();
        transport.Lifecycle.Should().Equal("receiving stopped", "acknowledged", "consumers stopped");
    }

    [TestMethod]
    public async Task StopWhenTheShutdownTokenExpires()
    {
        // Arrange
        await using var host = StartHost();
        var gate = host.GetRequiredService<DispatchGate>();
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        transport.Start();
        var message = EventMessage("stuck");
        _ = transport.DeliverAsync(message);
        await gate.Entered;

        try
        {
            // Act
            using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var stopwatch = Stopwatch.StartNew();
            await transport.StopAsync(shutdown.Token);
            stopwatch.Stop();

            // Assert -- stop gave up on the handler at the deadline and closed without its acknowledgement
            stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
            message.Acknowledged.Should().BeFalse();
            transport.Lifecycle.Should().Equal("receiving stopped", "consumers stopped");
        }
        finally
        {
            gate.Open();
        }
    }

    [TestMethod]
    public async Task AbandonAMessageDeliveredAfterStopBegins()
    {
        // Arrange -- a broker client can still hand over a message it fetched before it stopped receiving
        await using var host = StartHost();
        host.GetRequiredService<DispatchGate>().Open();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        var transport = new FakeBrokerTransport(host.GetRequiredService<ConduitOptions>(), host.Services);
        transport.Start();
        await transport.StopAsync(CancellationToken.None);
        var message = EventMessage("late");

        // Act
        await transport.DeliverAsync(message);

        // Assert -- not dispatched, and handed back to the broker for another consumer
        message.Abandoned.Should().BeTrue();
        message.Acknowledged.Should().BeFalse();
        spy.Count.Should().Be(0);
    }

    private static TestWebHost StartHost() => TestWebHost.Start(builder =>
    {
        builder.Services.AddSingleton<ItemStore>();
        builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
        builder.Services.AddSingleton<DispatchGate>();
        builder.Services.AddConduit(conduit => conduit.AddPipeline(typeof(GatedPipeline<,>)));
    });

    private static FakeBrokerMessage EventMessage(string name)
    {
        var envelope = TestEnvelope.For(new ItemCreatedEvent(Guid.NewGuid(), name));
        return new FakeBrokerMessage { Type = envelope.Type, Body = new BinaryData(envelope.Payload.GetRawText()) };
    }
}
