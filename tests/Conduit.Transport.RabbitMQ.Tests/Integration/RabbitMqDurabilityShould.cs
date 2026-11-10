using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ.Tests.Support.Contracts;
using Conduit.Transport.RabbitMQ.Tests.Support.Handlers;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Conduit.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// Tests that what the transport publishes outlives a broker restart: every message goes out persistent
/// (delivery mode 2), so a durable queue keeps it on disk.
/// </summary>
[TestClass]
public class RabbitMqDurabilityShould : RabbitMqTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.RabbitMQ.Tests.Support.Contracts";
    private const int MaxNameLength = 255;

    [TestMethod]
    [Timeout(180_000)]
    public async Task KeepAQueuedEventAcrossABrokerRestart()
    {
        // Arrange — Production topology (a durable exchange and queue) under a prefix of this test's own, and an
        // event queued for a listener whose host is down
        RequireRestartableBroker();
        var topologyPrefix = TestTopologyPrefix.NewForTest(typeof(RabbitMqDurabilityShould).Assembly);
        var listenerName = UniqueAppName("listener");

        try
        {
            await using (var firstListenerHost = await CreateProductionHostAsync(listenerName, topologyPrefix, producerOnly: false))
            {
                // Starting declared the listener's durable queue and bound it to the event; stopping leaves it
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
            await DeleteProductionTopologyAsync(listenerName, topologyPrefix);
        }
    }

    [TestMethod]
    public async Task PublishRequestsPersistent()
    {
        // Arrange — a handler, a sender routing to it, and a queue of the test's own that takes a copy of every
        // TestCommand the exchange routes
        await using var handlerHost = await CreateHostAsync(UniqueAppName("handler"));
        await using var senderHost = await CreateSenderHostAsync(
            UniqueAppName("sender"), handlerHost.TopologyPrefix!, ContractsNamespace, "broker");

        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        var probe = await channel.QueueDeclareAsync(string.Empty, durable: false, exclusive: true, autoDelete: true);
        await channel.QueueBindAsync(probe.QueueName, ExchangeName(handlerHost), typeof(TestCommand).FullName!);

        // Act
        await senderHost.GetRequiredService<IMediator>().ExecuteAsync(new TestCommand("probe"));

        // Assert — the request went out persistent
        var request = await GetMessageAsync(channel, probe.QueueName);
        request.BasicProperties.Persistent.Should().BeTrue();
    }

    [TestMethod]
    public async Task PublishRepliesPersistent()
    {
        // Arrange — a handler, and a requester of the test's own that reads its own reply queue
        await using var handlerHost = await CreateHostAsync(UniqueAppName("handler"));

        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        var replyQueue = await channel.QueueDeclareAsync(string.Empty, durable: false, exclusive: true, autoDelete: true);
        var requestProperties = new BasicProperties
        {
            Type = typeof(TestCommand).FullName,
            ReplyTo = replyQueue.QueueName,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };

        // Act — send a request the handler answers
        await channel.BasicPublishAsync(
            exchange: ExchangeName(handlerHost),
            routingKey: typeof(TestCommand).FullName!,
            mandatory: false,
            basicProperties: requestProperties,
            body: JsonSerializer.SerializeToUtf8Bytes(new TestCommand("probe"), ConduitRuntime.JsonOptions));

        // Assert — the reply went out persistent
        var reply = await GetMessageAsync(channel, replyQueue.QueueName);
        reply.BasicProperties.CorrelationId.Should().Be(requestProperties.CorrelationId);
        reply.BasicProperties.Persistent.Should().BeTrue();
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
                conduit.AddEndpoint("self", endpoint => endpoint.UseRabbitMQ(ConnectionString));
            });

            // The listener's one queue, bound to TestEvent alone: the topology the cleanup removes
            builder.Services.ConfigureConduitRegistrations(handling => handling
                .ClearAllHandlers()
                .HandleEvent<TestEvent, TestEventListener>());
        });
    }

    // Production topology is durable and never auto-deleted, so the test removes what it declared
    private static async Task DeleteProductionTopologyAsync(string listenerName, string topologyPrefix)
    {
        var conduitOptions = new ConduitOptions
        {
            ApplicationName = listenerName,
            TopologyMode = TopologyMode.Production,
            TopologyPrefix = topologyPrefix,
        };

        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeleteAsync(TopologyNaming.CreateAppResourceName(conduitOptions, MaxNameLength));
        await channel.QueueDeleteAsync(RabbitMQTopologyManager.DeadLetterName(conduitOptions));
        await channel.ExchangeDeleteAsync(RabbitMQTopologyManager.DeadLetterName(conduitOptions));
        await channel.ExchangeDeleteAsync(TopologyNaming.CreateSharedResourceName(conduitOptions, MaxNameLength));
    }

    private static string ExchangeName(TestHost host) =>
        TopologyNaming.CreateSharedResourceName(host.GetRequiredService<ConduitOptions>(), MaxNameLength);

    private static Task<IConnection> ConnectAsync() =>
        new ConnectionFactory { Uri = new Uri(ConnectionString!) }.CreateConnectionAsync();

    private static async Task<BasicGetResult> GetMessageAsync(IChannel channel, string queueName)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var message = await channel.BasicGetAsync(queueName, autoAck: true);
            if (message is not null)
            {
                return message;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"No message on '{queueName}' within 30 seconds.");
            }

            await Task.Delay(50);
        }
    }
}
