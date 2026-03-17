using AwesomeAssertions;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.AzureServiceBus.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.ServiceBus;

namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

[TestClass]
public class TransportIntegrationShould
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
    public async Task RoundTripCommandWithResult()
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
        var id = await mediator.ExecuteAsync(new CreateItemCommand("ASB Test"));

        // Assert
        id.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task RoundTripVoidCommand()
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

        var handlerStore = handlerHost.GetRequiredService<ItemStore>();
        var itemId = Guid.NewGuid();
        handlerStore.Add(itemId, "To Delete");

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
        await mediator.ExecuteAsync(new DeleteItemCommand(itemId));

        // Assert
        handlerStore.Get(itemId).Should().BeNull();
    }

    [TestMethod]
    public async Task RoundTripQuery()
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

        var handlerStore = handlerHost.GetRequiredService<ItemStore>();
        var itemId = Guid.NewGuid();
        handlerStore.Add(itemId, "Query Test");

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
        var result = await mediator.FetchAsync(new GetItemQuery(itemId));

        // Assert
        result.Should().Be("Query Test");
    }

    [TestMethod]
    public async Task PropagateNotFoundAsError()
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
        var action = () => mediator.FetchAsync(new GetItemQuery(Guid.NewGuid()));

        // Assert
        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.WithMessage("*NotFound*");
    }

    [TestMethod]
    public async Task PropagateGenericExceptionAsInternalError()
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
        var action = () => mediator.ExecuteAsync(new ExplodingCommand());

        // Assert
        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.WithMessage("*InternalError*");
    }
}
