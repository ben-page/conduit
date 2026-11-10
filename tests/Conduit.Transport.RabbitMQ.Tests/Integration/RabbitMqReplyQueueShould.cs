using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Transport.RabbitMQ.Tests.Support.Contracts;
using global::RabbitMQ.Client;

namespace Conduit.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// Tests the transport's reply queue: exclusive to its connection, under a per-instance name, and moved to
/// a fresh name when the one it wants is held by another connection.
/// </summary>
[TestClass]
public class RabbitMqReplyQueueShould : RabbitMqTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.RabbitMQ.Tests.Support.Contracts";

    [TestMethod]
    [Timeout(60_000)]
    public async Task FallBackToAFreshNameWhenTheNameIsLocked()
    {
        // Arrange — another connection already holds an exclusive queue under the name the sender's
        // reply queue would take, as a half-open old connection the broker has not timed out yet does
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");
        var instanceId = Guid.NewGuid().ToString("N");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        var lockedName = RabbitMQTopologyManager.ReplyQueueName(
            new ConduitOptions { ApplicationName = senderName, TopologyMode = TopologyMode.Test, TopologyPrefix = prefix },
            instanceId);

        var factory = new ConnectionFactory { Uri = new Uri(ConnectionString!) };
        await using var lockingConnection = await factory.CreateConnectionAsync();
        await using var lockingChannel = await lockingConnection.CreateChannelAsync();
        await lockingChannel.QueueDeclareAsync(lockedName, durable: false, exclusive: true, autoDelete: true);

        // Act — the sender starts with that name taken, then sends a request
        await using var senderHost = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker",
            options =>
            {
                options.InitialReplyQueueInstanceId = instanceId;
                options.ResponseTimeout = TimeSpan.FromSeconds(15);
            });
        var result = await senderHost.GetRequiredService<IMediator>().ExecuteAsync(new TestCommand("locked"));

        // Assert — the reply reached the request's waiter, so it was addressed to the fresh queue the
        // sender consumes, not the locked one (whose message count is still zero)
        result.Value.Should().Be("handled:locked");
        (await lockingChannel.MessageCountAsync(lockedName)).Should().Be(0);
    }
}
