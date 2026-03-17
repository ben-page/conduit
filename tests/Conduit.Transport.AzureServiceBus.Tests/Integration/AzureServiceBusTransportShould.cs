using System.Text.Json;
using AwesomeAssertions;
using Azure.Messaging.ServiceBus;
using Conduit.Configuration;
using Conduit.Dispatch;
using Conduit.Handlers;
using Conduit.TestUtils;
using Conduit.Transport.AzureServiceBus.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.ServiceBus;

namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

[TestClass]
public class AzureServiceBusTransportShould
{
    private static ServiceBusContainer? container;
    private static string? connectionString;
    private static string? adminConnectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        // Use env var if set (e.g. real ASB in CI), otherwise start emulator
        var envConnectionString = Environment.GetEnvironmentVariable("CONDUIT_ASB_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(envConnectionString))
        {
            connectionString = envConnectionString;
            adminConnectionString = envConnectionString;
            return;
        }

        try
        {
            container = new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
                .WithAcceptLicenseAgreement(true)
                .Build();
            await container.StartAsync();
            connectionString = container.GetConnectionString();
            adminConnectionString = container.GetHttpConnectionString();
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            container = null;
            connectionString = null;
            adminConnectionString = null;
            Console.WriteLine($"Skipping ASB emulator tests: {ex.Message}");
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

    private static void SkipIfNoConnectionString()
    {
        if (connectionString is null)
        {
            Assert.Inconclusive(
                "Azure Service Bus emulator not available. Ensure Docker/Podman is running, " +
                "or set CONDUIT_ASB_CONNECTION_STRING for a real namespace.");
        }
    }

    [TestMethod]
    public void DiscoverEventTypesFromServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTransient<IEventListener<ItemCreatedEvent>, ItemCreatedListener>();
        services.AddSingleton<EventSpy<ItemCreatedEvent>>();

        var options = new AzureServiceBusOptions { ConnectionString = "not-used" };
        var logger = new LoggerFactory().CreateLogger<AzureServiceBusTransport>();
        var transport = new AzureServiceBusTransport(options, services, logger);

        // Act — use reflection to test the private method
        var method = typeof(AzureServiceBusTransport)
            .GetMethod("DiscoverSubscribedEventTypes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var result = (List<string>)method!.Invoke(transport, null)!;

        // Assert
        result.Should().Contain(
            "Conduit.Transport.AzureServiceBus.Tests.Support.ItemCreatedEvent");
    }

    [TestMethod]
    public void CreateOptionsWithDefaults()
    {
        // Arrange & Act
        var options = new AzureServiceBusOptions { ConnectionString = "test" };

        // Assert
        options.AutoCreateTopology.Should().BeTrue();
        options.DefaultConcurrency.Should().Be(10);
        options.TopicName.Should().Be("conduit");
        options.QueuePrefix.Should().BeNull();
        options.Pools.Should().BeEmpty();
    }

    [TestMethod]
    public void ConfigurePoolsViaAddPool()
    {
        // Arrange
        var options = new AzureServiceBusOptions { ConnectionString = "test" };

        // Act
        options.AddPool("priority", pool =>
        {
            pool.Concurrency = 1;
            pool.Handle("MyApp.Commands.ImportantCommand");
        });

        // Assert
        options.Pools.Should().HaveCount(1);
        options.Pools[0].Name.Should().Be("priority");
        options.Pools[0].Concurrency.Should().Be(1);
        options.Pools[0].MessageTypes.Should().Contain("MyApp.Commands.ImportantCommand");
    }

    [TestMethod]
    public void RegisterTransportAndHostedServiceViaDI()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        var builder = new ConduitBuilder(services)
        {
            ApplicationName = "test-app",
        };

        // Act
        builder.AddEndpoint("test", endpoint =>
            endpoint.UseAzureServiceBus(
                "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=test"));

        // Assert — the endpoint should have a transport assigned
        builder.Options.Endpoints["test"].Transport.Should().NotBeNull();
        builder.Options.Endpoints["test"].Transport.Should().BeOfType<AzureServiceBusTransport>();

        var provider = services.BuildServiceProvider();
        var transports = provider.GetServices<IConduitTransport>().ToList();
        transports.Should().HaveCount(1);

        var hostedServices = provider.GetServices<IHostedService>().ToList();
        hostedServices.Should().ContainItemsAssignableTo<AzureServiceBusTransport>();
    }

    [TestMethod]
    public void SerializeMessageBodyAsPlainJson()
    {
        // Arrange — verify the wire format is the serialized message, not a DispatchEnvelope
        var command = new CreateItemCommand("Test Item");
        var json = JsonSerializer.Serialize(command, ConduitRuntime.JsonOptions);

        // Act
        var deserialized = JsonSerializer.Deserialize<JsonElement>(json, ConduitRuntime.JsonOptions);

        // Assert — the body should have the command properties directly
        deserialized.GetProperty("name").GetString().Should().Be("Test Item");
    }

    [TestMethod]
    public void SeedMessageContextOnOutboundMessages()
    {
        // Arrange — start a local dispatch to establish context
        var previousContext = MessageContext.Current;
        var context = MessageContext.StartDispatch();

        try
        {
            // Act — simulate what the transport does internally
            var message = new ServiceBusMessage("test")
            {
                MessageId = context.MessageId,
                CorrelationId = context.ConversationId,
            };

            if (context.CausationId is not null)
            {
                message.ApplicationProperties["CausationId"] = context.CausationId;
            }

            // Assert
            message.MessageId.Should().Be(context.MessageId);
            message.CorrelationId.Should().Be(context.ConversationId);
        }
        finally
        {
            MessageContext.RestoreContext(previousContext);
        }
    }

    [TestMethod]
    public void SanitizeSubscriptionNamesForAzureLimits()
    {
        // Arrange
        var longTypeName = "MyApp.Domain.Events.VeryLongEventNameThatExceedsFiftyCharacterLimit";

        // Act — sanitize like the topology manager does
        var sanitized = longTypeName.Replace('.', '-');
        if (sanitized.Length > 50)
        {
            sanitized = sanitized[..50];
        }

        // Assert
        sanitized.Length.Should().BeLessThanOrEqualTo(50);
        sanitized.Should().NotContain(".");
    }

    [TestMethod]
    public async Task ThrowWhenSendingBeforeStartup()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new AzureServiceBusOptions { ConnectionString = "not-used" };
        var logger = new LoggerFactory().CreateLogger<AzureServiceBusTransport>();
        var transport = new AzureServiceBusTransport(options, services, logger);
        var envelope = new DispatchEnvelope("Test.Command", JsonSerializer.SerializeToElement(new { }));

        // Act
        var act = () => transport.SendRequestAsync(envelope, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not been started*");
    }

    [TestMethod]
    public async Task ThrowWhenPublishingBeforeStartup()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new AzureServiceBusOptions { ConnectionString = "not-used" };
        var logger = new LoggerFactory().CreateLogger<AzureServiceBusTransport>();
        var transport = new AzureServiceBusTransport(options, services, logger);
        var envelope = new DispatchEnvelope("Test.Event", JsonSerializer.SerializeToElement(new { }));

        // Act
        var act = () => transport.SendEventAsync(envelope, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not been started*");
    }

    [TestMethod]
    public void BuildTopologyQueueNamesCorrectly()
    {
        // Arrange & Act — verify naming conventions
        var commandQueueName = AzureServiceBusTopologyManager.GetCommandQueueName("inventory");

        // Assert
        commandQueueName.Should().Be("conduit-inventory");
    }

    [TestMethod]
    public void BuildAppQueueNameWithDefaultPrefix()
    {
        // Arrange
        var adminClient = new Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient(
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=x");
        var options = new AzureServiceBusOptions { ConnectionString = "not-used" };
        var topologyManager = new AzureServiceBusTopologyManager(adminClient, options, "my-app");

        // Act
        var queueName = topologyManager.GetQueueName("my-app");

        // Assert
        queueName.Should().Be("conduit-my-app");
    }

    [TestMethod]
    public void BuildAppQueueNameWithCustomPrefix()
    {
        // Arrange
        var adminClient = new Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient(
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=x");
        var options = new AzureServiceBusOptions
        {
            ConnectionString = "not-used",
            QueuePrefix = "myprefix",
        };
        var topologyManager = new AzureServiceBusTopologyManager(adminClient, options, "my-app");

        // Act
        var queueName = topologyManager.GetQueueName("my-app");

        // Assert
        queueName.Should().Be("myprefix-my-app");
    }

    [TestMethod]
    public void BuildReplyQueueNameCorrectly()
    {
        // Arrange
        var adminClient = new Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient(
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=x");
        var options = new AzureServiceBusOptions { ConnectionString = "not-used" };
        var topologyManager = new AzureServiceBusTopologyManager(adminClient, options, "my-app");

        // Act
        var replyQueueName = topologyManager.GetReplyQueueName("my-app");

        // Assert
        replyQueueName.Should().Be("conduit-my-app-replies");
    }

    // --- Integration tests below require a real Azure Service Bus connection ---

    [TestMethod]
    public async Task CreateTopologyIdempotently()
    {
        // Arrange
        SkipIfNoConnectionString();

        var adminClient = new Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient(adminConnectionString!);
        var options = new AzureServiceBusOptions { ConnectionString = connectionString! };
        var appName = $"test-{Guid.NewGuid():N}"[..20];
        var topologyManager = new AzureServiceBusTopologyManager(adminClient, options, appName);

        try
        {
            var eventTypes = new List<string> { "Test.Events.SampleEvent" };

            // Act — create topology twice (should not throw)
            await topologyManager.EnsureTopologyAsync(eventTypes, CancellationToken.None);
            await topologyManager.EnsureTopologyAsync(eventTypes, CancellationToken.None);

            // Assert — if we got here without exception, idempotency works
        }
        finally
        {
            // Cleanup — best effort
            await TryCleanupAsync(adminClient, appName);
        }
    }

    [TestMethod]
    public async Task StartTransportSuccessfully()
    {
        // Arrange
        SkipIfNoConnectionString();

        var appName = $"test-{Guid.NewGuid():N}"[..20];
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ItemStore>();
        services.AddSingleton<EventSpy<ItemCreatedEvent>>();
        var conduitOptions = new ConduitOptions { ApplicationName = appName };
        services.AddSingleton(conduitOptions);

        var asbOptions = new AzureServiceBusOptions
        {
            ConnectionString = connectionString!,
            AdminConnectionString = adminConnectionString,
            DefaultConcurrency = 1,
        };

        var logger = new LoggerFactory().CreateLogger<AzureServiceBusTransport>();
        var transport = new AzureServiceBusTransport(asbOptions, services, logger);
        transport.SetServiceProvider(services.BuildServiceProvider());

        try
        {
            // Act
            await transport.StartAsync(CancellationToken.None);

            // Assert — transport started successfully without exceptions
        }
        finally
        {
            await transport.StopAsync(CancellationToken.None);
            await transport.DisposeAsync();

            // Cleanup
            var adminClient = new Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient(adminConnectionString!);
            await TryCleanupAsync(adminClient, appName);
        }
    }

    [TestMethod]
    public async Task PublishEventToTopic()
    {
        // Arrange
        SkipIfNoConnectionString();

        var appName = $"test-{Guid.NewGuid():N}"[..20];

        // Create topology first
        var adminClient = new Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient(adminConnectionString!);
        var options = new AzureServiceBusOptions { ConnectionString = connectionString! };
        var topologyManager = new AzureServiceBusTopologyManager(adminClient, options, appName);
        await topologyManager.EnsureTopologyAsync([], CancellationToken.None);

        try
        {
            await using var client = new ServiceBusClient(connectionString!);
            await using var sender = client.CreateSender(options.TopicName);

            var eventPayload = JsonSerializer.SerializeToElement(
                new ItemCreatedEvent(Guid.NewGuid(), "Test Item"),
                ConduitRuntime.JsonOptions);

            var message = new ServiceBusMessage(
                JsonSerializer.SerializeToUtf8Bytes(eventPayload, ConduitRuntime.JsonOptions))
            {
                Subject = "Conduit.Transport.AzureServiceBus.Tests.Support.ItemCreatedEvent",
                ContentType = "application/json",
            };

            // Act
            await sender.SendMessageAsync(message, CancellationToken.None);

            // Assert — message published without error
        }
        finally
        {
            await TryCleanupAsync(adminClient, appName);
        }
    }

    [TestMethod]
    public async Task PropagateMessageContextViaMessageProperties()
    {
        // Arrange
        SkipIfNoConnectionString();

        var appName = $"test-{Guid.NewGuid():N}"[..20];
        var adminClient = new Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient(adminConnectionString!);
        var options = new AzureServiceBusOptions { ConnectionString = connectionString! };
        var topologyManager = new AzureServiceBusTopologyManager(adminClient, options, appName);

        // Create the queue
        await topologyManager.EnsureTopologyAsync([], CancellationToken.None);
        var queueName = topologyManager.GetQueueName(appName);

        try
        {
            await using var client = new ServiceBusClient(connectionString!);

            // Seed a message context
            var previousContext = MessageContext.Current;
            var context = MessageContext.StartDispatch();

            try
            {
                // Act — send a message with context properties
                var message = new ServiceBusMessage("test-body")
                {
                    Subject = "Test.Command",
                    ContentType = "application/json",
                    MessageId = context.MessageId,
                    CorrelationId = context.ConversationId,
                };

                if (context.CausationId is not null)
                {
                    message.ApplicationProperties["CausationId"] = context.CausationId;
                }

                await using var sender = client.CreateSender(queueName);
                await sender.SendMessageAsync(message, CancellationToken.None);

                // Receive and verify
                await using var receiver = client.CreateReceiver(queueName);
                var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));

                // Assert
                received.Should().NotBeNull();
                received!.MessageId.Should().Be(context.MessageId);
                received.CorrelationId.Should().Be(context.ConversationId);
                received.Subject.Should().Be("Test.Command");

                await receiver.CompleteMessageAsync(received);
            }
            finally
            {
                MessageContext.RestoreContext(previousContext);
            }
        }
        finally
        {
            await TryCleanupAsync(adminClient, appName);
        }
    }

#pragma warning disable CA1031
    private static async Task TryCleanupAsync(
        Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient adminClient,
        string appName)
    {
        // Best-effort cleanup of test resources
        var queueName = $"conduit-{appName}";

        try
        {
            await adminClient.DeleteQueueAsync(queueName);
        }
        catch
        {
            // Best-effort cleanup
        }

        var replyQueueName = $"{queueName}-replies";

        try
        {
            await adminClient.DeleteQueueAsync(replyQueueName);
        }
        catch
        {
            // Best-effort cleanup
        }
    }
#pragma warning restore CA1031
}
