using AwesomeAssertions;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.AzureServiceBus.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.ServiceBus;

namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

[TestClass]
public class EventDeliveryShould
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
    public async Task DeliverEventToListener()
    {
        // Arrange
        SkipIfNoConnectionString();

        var appName = $"asb-{Guid.NewGuid():N}"[..15];

        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = appName;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseAzureServiceBus(connectionString!, options =>
                        options.AdminConnectionString = adminConnectionString));
            });
        });

        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Test Item"));

        // Assert
        await spy.WaitForCountAsync(1, TimeSpan.FromSeconds(15));
        spy.Events[0].Name.Should().Be("Test Item");
    }

    [TestMethod]
    public async Task ContinueProcessingAfterListenerFailure()
    {
        // Arrange
        SkipIfNoConnectionString();

        var appName = $"asb-{Guid.NewGuid():N}"[..15];

        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = appName;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseAzureServiceBus(connectionString!, options =>
                        options.AdminConnectionString = adminConnectionString));
            });
        });

        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act — publish an event whose listener throws, then a normal event
        await mediator.PublishAsync(new ExplodingEvent("boom"));
        await Task.Delay(2000);
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "After Failure"));

        // Assert — the second event should still be delivered
        await spy.WaitForCountAsync(1, TimeSpan.FromSeconds(15));
        spy.Events[0].Name.Should().Be("After Failure");
    }
}
