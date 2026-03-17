using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ.Tests.Support;
using Conduit.Generated;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.RabbitMq;

namespace Conduit.Transport.RabbitMQ.Tests.Integration;

[TestClass]
public class TransportIntegrationShould
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
        catch (Exception ex)
        {
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

    [TestMethod]
    public async Task RoundTripCommandWithResult()
    {
        // Arrange
        SkipIfNoRabbitMQ();
        var handlerAppName = $"rmq-{Guid.NewGuid():N}"[..15];
        var callerAppName = $"rmq-{Guid.NewGuid():N}"[..15];

        await using var handlerHost = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = handlerAppName;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseRabbitMQ(connectionString!));
            });
        });

        await using var callerHost = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = callerAppName;
                conduit.AddEndpoint(handlerAppName, endpoint =>
                    endpoint.UseRabbitMQ(connectionString!));
                conduit.Route("Conduit.Transport.RabbitMQ.Tests.Support").To(handlerAppName);
            });
        });

        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var id = await mediator.ExecuteAsync(new CreateItemCommand("RMQ Test"));

        // Assert
        id.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task RoundTripVoidCommand()
    {
        // Arrange
        SkipIfNoRabbitMQ();
        var handlerAppName = $"rmq-{Guid.NewGuid():N}"[..15];
        var callerAppName = $"rmq-{Guid.NewGuid():N}"[..15];

        await using var handlerHost = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = handlerAppName;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseRabbitMQ(connectionString!));
            });
        });

        var handlerStore = handlerHost.GetRequiredService<ItemStore>();
        var id = Guid.NewGuid();
        handlerStore.Add(id, "To Delete");

        await using var callerHost = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = callerAppName;
                conduit.AddEndpoint(handlerAppName, endpoint =>
                    endpoint.UseRabbitMQ(connectionString!));
                conduit.Route("Conduit.Transport.RabbitMQ.Tests.Support").To(handlerAppName);
            });
        });

        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new DeleteItemCommand(id));

        // Assert
        handlerStore.Get(id).Should().BeNull();
    }

    [TestMethod]
    public async Task RoundTripQuery()
    {
        // Arrange
        SkipIfNoRabbitMQ();
        var handlerAppName = $"rmq-{Guid.NewGuid():N}"[..15];
        var callerAppName = $"rmq-{Guid.NewGuid():N}"[..15];

        await using var handlerHost = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = handlerAppName;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseRabbitMQ(connectionString!));
            });
        });

        var itemId = Guid.NewGuid();
        handlerHost.GetRequiredService<ItemStore>().Add(itemId, "Query Test Item");

        await using var callerHost = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = callerAppName;
                conduit.AddEndpoint(handlerAppName, endpoint =>
                    endpoint.UseRabbitMQ(connectionString!));
                conduit.Route("Conduit.Transport.RabbitMQ.Tests.Support").To(handlerAppName);
            });
        });

        var mediator = callerHost.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.FetchAsync(new GetItemQuery(itemId));

        // Assert
        result.Should().Be("Query Test Item");
    }

    [TestMethod]
    public async Task PropagateNotFoundAsError()
    {
        // Arrange
        SkipIfNoRabbitMQ();
        var handlerAppName = $"rmq-{Guid.NewGuid():N}"[..15];
        var callerAppName = $"rmq-{Guid.NewGuid():N}"[..15];

        await using var handlerHost = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = handlerAppName;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseRabbitMQ(connectionString!));
            });
        });

        await using var callerHost = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = callerAppName;
                conduit.AddEndpoint(handlerAppName, endpoint =>
                    endpoint.UseRabbitMQ(connectionString!));
                conduit.Route("Conduit.Transport.RabbitMQ.Tests.Support").To(handlerAppName);
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
        SkipIfNoRabbitMQ();
        var handlerAppName = $"rmq-{Guid.NewGuid():N}"[..15];
        var callerAppName = $"rmq-{Guid.NewGuid():N}"[..15];

        await using var handlerHost = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = handlerAppName;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseRabbitMQ(connectionString!));
            });
        });

        await using var callerHost = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = callerAppName;
                conduit.AddEndpoint(handlerAppName, endpoint =>
                    endpoint.UseRabbitMQ(connectionString!));
                conduit.Route("Conduit.Transport.RabbitMQ.Tests.Support").To(handlerAppName);
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
