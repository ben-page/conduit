using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ.Tests.Support;
using Conduit.Generated;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.RabbitMq;

namespace Conduit.Transport.RabbitMQ.Tests.Integration;

[TestClass]
public class MessageContextShould
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
    public async Task PropagateConversationIdThroughCommandDispatch()
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

        // Capture the caller-side context so we can verify it propagated
        var callerContext = MessageContext.StartDispatch();

        // Act
        var snapshot = await mediator.ExecuteAsync(new CaptureContextCommand());

        // Assert — handler should see the same ConversationId the caller established
        snapshot.MessageId.Should().NotBeNullOrEmpty();
        snapshot.ConversationId.Should().Be(callerContext.ConversationId);
        snapshot.CausationId.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task AssignFreshMessageIdOnHandlerSide()
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

        // Act — dispatch two commands and verify they get distinct MessageIds
        var snapshot1 = await mediator.ExecuteAsync(new CaptureContextCommand());
        var snapshot2 = await mediator.ExecuteAsync(new CaptureContextCommand());

        // Assert
        snapshot1.MessageId.Should().NotBe(snapshot2.MessageId);
    }
}
