using System.Text.Json;
using Amqp;
using Amqp.Framing;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ.Tests.Support;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;
using Conduit.Transport.ActiveMQ.Tests.Support.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.ActiveMQ.Tests.Integration;

/// <summary>
/// Tests that what the transport sends outlives a broker restart: every message goes out with an AMQP header
/// marking it durable, so Artemis writes it to its journal when a durable queue takes it.
/// </summary>
[TestClass]
public class ActiveMQDurabilityShould : ActiveMQTestBase
{
    private const int MaxNameLength = 256;

    [TestMethod]
    [Timeout(300_000)]
    public async Task KeepAQueuedEventAcrossABrokerRestart()
    {
        // Arrange — Production topology (a durable queue) under a prefix of this test's own, and an event queued
        // for a listener whose host is down
        RequireRestartableBroker();
        var topologyPrefix = TestTopologyPrefix.NewForTest(typeof(ActiveMQDurabilityShould).Assembly);
        var listenerName = UniqueAppName("listener");

        try
        {
            await using (var firstListenerHost = await CreateProductionHostAsync(listenerName, topologyPrefix, producerOnly: false))
            {
                // Starting created the listener's durable event queue; stopping leaves it
            }

            await using (var publisherHost = await CreateProductionHostAsync(UniqueAppName("publisher"), topologyPrefix, producerOnly: true))
            {
                await publisherHost.GetRequiredService<IMediator>().PublishAsync(new TestEvent("queued"));
            }

            // Act — restart the broker, then the listener
            await RestartBrokerAsync();
            await using var listenerHost = await CreateProductionHostAsync(listenerName, topologyPrefix, producerOnly: false);

            // Assert — the event was still in the listener's queue
            var listenerSpy = listenerHost.GetRequiredService<SpyLog>();
            await listenerSpy.WaitForCountAsync(1, TimeSpan.FromSeconds(30));
            listenerSpy.Invocations.OfType<TestEvent>().Should().ContainSingle()
                .Which.Value.Should().Be("queued");
        }
        finally
        {
            await DeleteProductionTopologyAsync(topologyPrefix);
        }
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task SendRepliesDurable()
    {
        // Arrange — a handler, and a requester of the test's own that reads its own temporary reply queue
        await using var handlerHost = await CreateHostAsync(UniqueAppName("handler"));
        var commandsAddress = TopologyNaming.CreateSharedResourceName(
            handlerHost.GetRequiredService<ConduitOptions>(), "commands", MaxNameLength);

        var connection = await new ConnectionFactory().CreateAsync(new Address(ConnectionString));
        try
        {
            var session = new Session(connection);
            var replyNodeAttached = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replyReceiver = new ReceiverLink(
                session,
                $"test-reply-{Guid.NewGuid():N}",
                new Source { Dynamic = true },
                (link, attach) => replyNodeAttached.TrySetResult(((Source)attach.Source).Address));
            var replyAddress = await replyNodeAttached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var requestSender = new SenderLink(session, $"test-request-{Guid.NewGuid():N}", commandsAddress);

            var correlationId = Guid.NewGuid().ToString("N");
            using var request = new Message(JsonSerializer.SerializeToUtf8Bytes(new TestCommand("probe"), ConduitRuntime.JsonOptions))
            {
                Properties = new Properties { Subject = typeof(TestCommand).FullName },
                ApplicationProperties = new ApplicationProperties
                {
                    ["ReplyTo"] = replyAddress,
                    ["ReplyCorrelationId"] = correlationId,
                },
            };

            // Act — send a request the handler answers
            await requestSender.SendAsync(request);
            using var reply = await replyReceiver.ReceiveAsync(TimeSpan.FromSeconds(30))
                ?? throw new TimeoutException("No reply within 30 seconds.");
            replyReceiver.Accept(reply);

            // Assert — the reply went out durable
            reply.Properties.CorrelationId.Should().Be(correlationId);
            reply.Header.Should().NotBeNull();
            reply.Header.Durable.Should().BeTrue();
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static Task<TestHost> CreateProductionHostAsync(
        string applicationName,
        string topologyPrefix,
        bool producerOnly)
    {
        return TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = applicationName;
                conduit.TopologyMode = TopologyMode.Production;
                conduit.TopologyPrefix = topologyPrefix;
                conduit.ProducerOnly = producerOnly;
                conduit.AddEndpoint("self", endpoint => endpoint.UseActiveMQ(ConnectionString));
            });

            // The listener's one queue, filtered to TestEvent alone: the topology the cleanup removes
            builder.Services.ConfigureConduitRegistrations(handling => handling
                .ClearAllHandlers()
                .HandleEvent<TestEvent, TestEventListener>());
        });
    }

    // Production queues are durable and never auto-deleted, so the test removes what it created: deleting the
    // two addresses, forced, deletes the queues bound to them
    private static async Task DeleteProductionTopologyAsync(string topologyPrefix)
    {
        var conduitOptions = new ConduitOptions { TopologyMode = TopologyMode.Production, TopologyPrefix = topologyPrefix };

        await using var management = await ArtemisManagement.ConnectAsync(ConnectionString!);
        await management.DeleteAddressAsync(TopologyNaming.CreateSharedResourceName(conduitOptions, "events", MaxNameLength));
        await management.DeleteAddressAsync(TopologyNaming.CreateSharedResourceName(conduitOptions, "commands", MaxNameLength));
    }
}
