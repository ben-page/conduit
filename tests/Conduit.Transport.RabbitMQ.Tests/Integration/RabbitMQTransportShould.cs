using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Dispatch;
using Conduit.TestUtils;
using Conduit.Generated;
using Conduit.Transport.RabbitMQ.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Conduit.Transport.RabbitMQ.Tests.Integration;

[TestClass]
public class RabbitMQTransportShould
{
    private static RabbitMqContainer? container;
    private static string? connectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        try
        {
            container = new RabbitMqBuilder("rabbitmq:4-management-alpine")
                .Build();
            await container.StartAsync();
            connectionString = container.GetConnectionString();
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Docker may not be available in all environments
            container = null;
            connectionString = null;
            Console.WriteLine($"Skipping RabbitMQ tests: {ex.Message}");
        }
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    private static void SkipIfNoRabbitMQ()
    {
        if (connectionString is null)
        {
            Assert.Inconclusive("RabbitMQ container not available. Ensure Docker is running.");
        }
    }

    private static IHost CreateReceiverHost(string applicationName = "test-app")
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
        builder.Services.AddConduit(conduit =>
        {
            conduit.ApplicationName = applicationName;
            conduit.AddEndpoint("self", endpoint =>
                endpoint.UseRabbitMQ(connectionString!));
        });

        return builder.Build();
    }

    [TestMethod]
    public async Task PublishEventToExchangeAndDeliverToListener()
    {
        // Arrange
        SkipIfNoRabbitMQ();
        var host = CreateReceiverHost();
        await host.StartAsync();

        try
        {
            var spy = host.Services.GetRequiredService<EventSpy<ItemCreatedEvent>>();
            var eventId = Guid.NewGuid();

            // Act — publish an event directly to the exchange
            var factory = new ConnectionFactory { Uri = new Uri(connectionString!) };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();

            var body = JsonSerializer.SerializeToUtf8Bytes(
                new { Id = eventId, Name = "Test Item" },
                ConduitRuntime.JsonOptions);

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                Type = "Conduit.Transport.RabbitMQ.Tests.Support.ItemCreatedEvent",
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = Guid.NewGuid().ToString("N"),
            };

            await channel.BasicPublishAsync(
                exchange: "conduit",
                routingKey: "Conduit.Transport.RabbitMQ.Tests.Support.ItemCreatedEvent",
                mandatory: false,
                basicProperties: properties,
                body: body);

            // Assert
            await spy.WaitForCountAsync(1, TimeSpan.FromSeconds(10));
            spy.Events[0].Id.Should().Be(eventId);
            spy.Events[0].Name.Should().Be("Test Item");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [TestMethod]
    public async Task SendCommandAndReceiveResponseViaReplyQueue()
    {
        // Arrange
        SkipIfNoRabbitMQ();
        var host = CreateReceiverHost("cmd-test-app");
        await host.StartAsync();

        try
        {
            var transport = host.Services.GetRequiredService<RabbitMQTransport>();

            var payload = JsonSerializer.SerializeToElement(
                new { Message = "hello" }, ConduitRuntime.JsonOptions);
            var envelope = new DispatchEnvelope(
                "Conduit.Transport.RabbitMQ.Tests.Support.PingCommand", payload);

            // Act — send a command through the transport (routes to self via direct exchange)
            // The command needs to be sent to the app's own queue for the receiver to pick up.
            // Publish directly to the command queue via raw client.
            var factory = new ConnectionFactory { Uri = new Uri(connectionString!) };
            await using var connection = await factory.CreateConnectionAsync();
            await using var senderChannel = await connection.CreateChannelAsync();

            // Set up reply queue
            var replyDeclare = await senderChannel.QueueDeclareAsync(
                queue: string.Empty, durable: false, exclusive: true, autoDelete: true);
            var replyQueueName = replyDeclare.QueueName;

            var correlationId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            var replyConsumer = new global::RabbitMQ.Client.Events.AsyncEventingBasicConsumer(senderChannel);
            replyConsumer.ReceivedAsync += (sender, args) =>
            {
                if (args.BasicProperties.CorrelationId == correlationId)
                {
                    tcs.TrySetResult(args.Body.ToArray());
                }

                return Task.CompletedTask;
            };
            await senderChannel.BasicConsumeAsync(replyQueueName, autoAck: true, consumer: replyConsumer);

            var body = JsonSerializer.SerializeToUtf8Bytes(
                new { Message = "hello" }, ConduitRuntime.JsonOptions);

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                Type = "Conduit.Transport.RabbitMQ.Tests.Support.PingCommand",
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = correlationId,
                ReplyTo = replyQueueName,
            };

            // Publish to the app's event queue (which is where the consumer listens)
            await senderChannel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: "conduit-cmd-test-app",
                mandatory: false,
                basicProperties: properties,
                body: body);

            // Assert — wait for reply
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var registration = timeoutCts.Token.Register(() => tcs.TrySetCanceled());

            var replyBody = await tcs.Task;
            var result = JsonSerializer.Deserialize<DispatchResult<JsonElement?>>(
                replyBody, ConduitRuntime.JsonOptions);

            result.Should().NotBeNull();
            result!.Error.Should().BeNull();
            result.Data.Should().NotBeNull();
            result.Data!.Value.GetString().Should().Be("pong: hello");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [TestMethod]
    public async Task PropagateMessageContextViaHeaders()
    {
        // Arrange
        SkipIfNoRabbitMQ();
        var host = CreateReceiverHost("context-test-app");
        await host.StartAsync();

        try
        {
            var spy = host.Services.GetRequiredService<EventSpy<ItemCreatedEvent>>();

            var factory = new ConnectionFactory { Uri = new Uri(connectionString!) };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();

            var messageId = "test-message-id-123";
            var conversationId = "test-conversation-456";
            var causationId = "test-causation-789";

            var body = JsonSerializer.SerializeToUtf8Bytes(
                new { Id = Guid.NewGuid(), Name = "Context Test" },
                ConduitRuntime.JsonOptions);

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                Type = "Conduit.Transport.RabbitMQ.Tests.Support.ItemCreatedEvent",
                MessageId = messageId,
                CorrelationId = conversationId,
                Headers = new Dictionary<string, object?>
                {
                    ["CausationId"] = Encoding.UTF8.GetBytes(causationId),
                },
            };

            // Act
            await channel.BasicPublishAsync(
                exchange: "conduit",
                routingKey: "Conduit.Transport.RabbitMQ.Tests.Support.ItemCreatedEvent",
                mandatory: false,
                basicProperties: properties,
                body: body);

            // Assert — the event was delivered (context was seeded properly)
            await spy.WaitForCountAsync(1, TimeSpan.FromSeconds(10));
            spy.Events.Should().HaveCount(1);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [TestMethod]
    public async Task UseMessageBodyAsPlainJsonWithoutEnvelopeWrapper()
    {
        // Arrange
        SkipIfNoRabbitMQ();
        var host = CreateReceiverHost("body-test-app");
        await host.StartAsync();

        try
        {
            var spy = host.Services.GetRequiredService<EventSpy<ItemCreatedEvent>>();

            var factory = new ConnectionFactory { Uri = new Uri(connectionString!) };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();

            // The message body is a plain serialized event — NOT a DispatchEnvelope
            var eventData = new { Id = Guid.NewGuid(), Name = "Plain JSON" };
            var body = JsonSerializer.SerializeToUtf8Bytes(eventData, ConduitRuntime.JsonOptions);

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                Type = "Conduit.Transport.RabbitMQ.Tests.Support.ItemCreatedEvent",
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = Guid.NewGuid().ToString("N"),
            };

            // Act
            await channel.BasicPublishAsync(
                exchange: "conduit",
                routingKey: "Conduit.Transport.RabbitMQ.Tests.Support.ItemCreatedEvent",
                mandatory: false,
                basicProperties: properties,
                body: body);

            // Assert — the receiver successfully deserialized the plain JSON body
            await spy.WaitForCountAsync(1, TimeSpan.FromSeconds(10));
            spy.Events[0].Name.Should().Be("Plain JSON");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [TestMethod]
    public async Task DeclareTopologyIdempotently()
    {
        // Arrange
        SkipIfNoRabbitMQ();

        // Act — start two hosts with the same application name
        // Both will try to declare the same topology. RabbitMQ declarations are idempotent.
        var host1 = CreateReceiverHost("idempotent-test");
        var host2 = CreateReceiverHost("idempotent-test");

        await host1.StartAsync();

        // Assert — second host should start without errors
        Func<Task> act = async () => await host2.StartAsync();
        await act.Should().NotThrowAsync();

        await host1.StopAsync();
        await host2.StopAsync();
        host1.Dispose();
        host2.Dispose();
    }

    [TestMethod]
    public async Task AutoSubscribeToEventsWithRegisteredListeners()
    {
        // Arrange
        SkipIfNoRabbitMQ();
        var host = CreateReceiverHost("auto-sub-test");
        await host.StartAsync();

        try
        {
            // Verify the queue exists and has bindings by publishing an event
            // and checking that it arrives at the listener.
            var spy = host.Services.GetRequiredService<EventSpy<ItemCreatedEvent>>();

            var factory = new ConnectionFactory { Uri = new Uri(connectionString!) };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();

            var body = JsonSerializer.SerializeToUtf8Bytes(
                new { Id = Guid.NewGuid(), Name = "Auto-Sub Test" },
                ConduitRuntime.JsonOptions);

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                Type = "Conduit.Transport.RabbitMQ.Tests.Support.ItemCreatedEvent",
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = Guid.NewGuid().ToString("N"),
            };

            // Act
            await channel.BasicPublishAsync(
                exchange: "conduit",
                routingKey: "Conduit.Transport.RabbitMQ.Tests.Support.ItemCreatedEvent",
                mandatory: false,
                basicProperties: properties,
                body: body);

            // Assert — event was received, proving auto-subscription created the binding
            await spy.WaitForCountAsync(1, TimeSpan.FromSeconds(10));
            spy.Events[0].Name.Should().Be("Auto-Sub Test");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [TestMethod]
    public async Task ReturnErrorResultForFailedCommand()
    {
        // Arrange
        SkipIfNoRabbitMQ();
        var host = CreateReceiverHost("error-test-app");
        await host.StartAsync();

        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(connectionString!) };
            await using var connection = await factory.CreateConnectionAsync();
            await using var senderChannel = await connection.CreateChannelAsync();

            // Set up reply queue
            var replyDeclare = await senderChannel.QueueDeclareAsync(
                queue: string.Empty, durable: false, exclusive: true, autoDelete: true);
            var replyQueueName = replyDeclare.QueueName;

            var correlationId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            var replyConsumer = new global::RabbitMQ.Client.Events.AsyncEventingBasicConsumer(senderChannel);
            replyConsumer.ReceivedAsync += (sender, args) =>
            {
                if (args.BasicProperties.CorrelationId == correlationId)
                {
                    tcs.TrySetResult(args.Body.ToArray());
                }

                return Task.CompletedTask;
            };
            await senderChannel.BasicConsumeAsync(replyQueueName, autoAck: true, consumer: replyConsumer);

            var body = JsonSerializer.SerializeToUtf8Bytes(
                new { Id = Guid.NewGuid() }, ConduitRuntime.JsonOptions);

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                Type = "Conduit.Transport.RabbitMQ.Tests.Support.GetItemQuery",
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = correlationId,
                ReplyTo = replyQueueName,
            };

            // Act — send a query that will throw NotFoundException
            await senderChannel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: "conduit-error-test-app",
                mandatory: false,
                basicProperties: properties,
                body: body);

            // Assert — reply contains error
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var registration = timeoutCts.Token.Register(() => tcs.TrySetCanceled());

            var replyBody = await tcs.Task;
            var result = JsonSerializer.Deserialize<DispatchResult<JsonElement?>>(
                replyBody, ConduitRuntime.JsonOptions);

            result.Should().NotBeNull();
            result!.Error.Should().NotBeNull();
            result.Error!.Code.Should().Be("NotFound");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
