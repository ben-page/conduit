using AwesomeAssertions;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.AzureServiceBus.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.ServiceBus;

namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

[TestClass]
public class MessageContextShould
{
    private static ServiceBusContainer? container;
    private static string? connectionString;
    private static string? adminConnectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
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
        catch (Exception ex)
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
    public async Task PropagateContextThroughCommandDispatch()
    {
        // Arrange
        SkipIfNoConnectionString();

        var handlerAppName = $"asb-{Guid.NewGuid():N}"[..15];
        var callerAppName = $"asb-{Guid.NewGuid():N}"[..15];

        await using var handlerHost = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = handlerAppName;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseAzureServiceBus(connectionString!, options =>
                        options.AdminConnectionString = adminConnectionString));
            });
        });

        await using var callerHost = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = callerAppName;
                conduit.AddEndpoint(handlerAppName, endpoint =>
                    endpoint.UseAzureServiceBus(connectionString!, options =>
                        options.AdminConnectionString = adminConnectionString));
                conduit.Route("Conduit.Transport.AzureServiceBus.Tests.Support").To(handlerAppName);
            });
        });

        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var snapshot = await mediator.ExecuteAsync(new CaptureContextCommand());

        // Assert
        snapshot.MessageId.Should().NotBeNullOrEmpty();
        snapshot.ConversationId.Should().NotBeNullOrEmpty();
    }
}
