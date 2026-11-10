using System.Diagnostics;
using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.ActiveMQ.Tests.Integration;

/// <summary>
/// Tests what a lost connection does to the transport: AMQPNetLite does not reconnect, so the transport
/// rebuilds its connection, topology, links and reply queue itself.
/// </summary>
[TestClass]
public class ActiveMQReconnectShould : ActiveMQTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.ActiveMQ.Tests.Support.Contracts";

    [TestMethod]
    [Timeout(300_000)]
    public async Task ResumeAfterBrokerRestart()
    {
        // Arrange — a sender and a handler that have already exchanged one request and its reply
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker",
            options => options.ResponseTimeout = TimeSpan.FromSeconds(15));

        var mediator = senderHost.GetRequiredService<IMediator>();
        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        (await mediator.ExecuteAsync(new TestCommand("before"))).Value.Should().Be("handled:before");

        var handlerReconnected = handlerHost.Services.GetRequiredKeyedService<ActiveMQTransport>("self").NextReconnection;
        var senderReconnected = senderHost.Services.GetRequiredKeyedService<ActiveMQTransport>("broker").NextReconnection;

        // Act — restart the broker: both connections drop, and with them every link, the sender's reply
        // queue and (in Test mode, not durable) the handler's queues. Once both transports have rebuilt
        // what they had, send a request and publish an event.
        await RestartBrokerAsync();
        await Task.WhenAll(handlerReconnected, senderReconnected).WaitAsync(TimeSpan.FromSeconds(120));
        var result = await mediator.ExecuteAsync(new TestCommand("after"));
        await mediator.PublishAsync(new TestEvent("after"));

        // Assert — the request reached the handler's re-created command queue and its reply the sender's new
        // reply queue, and the event the handler's re-created event queue
        result.Value.Should().Be("handled:after");
        await handlerSpy.WaitForCountAsync(3, TimeSpan.FromSeconds(15));
        handlerSpy.Invocations.OfType<TestCommand>().Select(command => command.Input)
            .Should().Equal("before", "after");
        handlerSpy.Invocations.OfType<TestEvent>().Should().ContainSingle()
            .Which.Value.Should().Be("after");
    }

    [TestMethod]
    [Timeout(300_000)]
    public async Task FailARequestAwaitingItsReplyWhenTheConnectionIsLost()
    {
        // Arrange — a request whose handler is still running
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker",
            options => options.ResponseTimeout = TimeSpan.FromSeconds(30));

        var senderTransport = senderHost.Services.GetRequiredKeyedService<ActiveMQTransport>("broker");
        var reply = senderHost.GetRequiredService<IMediator>().ExecuteAsync(new ShutdownProbeCommand("lost", DelayMs: 5000));
        await handlerHost.GetRequiredService<SpyLog>().WaitForCountAsync(1, TimeSpan.FromSeconds(30));
        var senderReconnected = senderTransport.NextReconnection;

        try
        {
            // Act — the broker goes away while the request waits for its reply
            var stopwatch = Stopwatch.StartNew();
            await StopBrokerAsync();
            var failure = await reply.Awaiting(request => request).Should().ThrowExactlyAsync<InvalidOperationException>();
            stopwatch.Stop();

            // Assert — the reply queue went with the connection, so the request failed then, not at its
            // 30-second response timeout
            failure.WithMessage("The connection to the broker for endpoint 'broker' was lost before the reply arrived.");
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await StartBrokerAsync();
        }

        await senderReconnected.WaitAsync(TimeSpan.FromSeconds(120));
    }

    [TestMethod]
    [Timeout(300_000)]
    public async Task StopWhileReconnecting()
    {
        // Arrange — a host whose broker has gone away, so its transport is waiting to retry
        await using var host = await CreateHostAsync(UniqueAppName("handler"));
        var transport = host.Services.GetRequiredKeyedService<ActiveMQTransport>("self");

        await StopBrokerAsync();
        try
        {
            await WaitUntilAsync(() => !transport.IsConnected, TimeSpan.FromSeconds(30));

            // Act
            var stopwatch = Stopwatch.StartNew();
            await host.DisposeAsync();
            stopwatch.Stop();

            // Assert — stop did not wait for the broker, and the reconnection it overtook gave up without
            // installing a connection
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
            transport.IsConnected.Should().BeFalse();
            transport.NextReconnection.IsCompleted.Should().BeFalse();
        }
        finally
        {
            await StartBrokerAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"The condition did not hold within {timeout}.");
            }

            await Task.Delay(50);
        }
    }
}
