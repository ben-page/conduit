using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ.Tests.Support;
using Conduit.Generated;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.RabbitMq;

namespace Conduit.Transport.RabbitMQ.Tests.Integration;

[TestClass]
public class EventDeliveryShould
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
    public async Task DeliverEventToListener()
    {
        // Arrange
        SkipIfNoRabbitMQ();
        var appName = $"rmq-{Guid.NewGuid():N}"[..15];

        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = appName;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseRabbitMQ(connectionString!));
            });
        });

        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Event Test"));

        // Assert
        await spy.WaitForCountAsync(1, TimeSpan.FromSeconds(10));
        spy.Events[0].Name.Should().Be("Event Test");
    }

    [TestMethod]
    public async Task ContinueProcessingAfterListenerFailure()
    {
        // Arrange
        SkipIfNoRabbitMQ();
        var appName = $"rmq-{Guid.NewGuid():N}"[..15];

        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = appName;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseRabbitMQ(connectionString!));
            });
        });

        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        var mediator = host.GetRequiredService<IMediator>();

        // Act — publish an event that causes a listener failure, then a normal event
        await mediator.PublishAsync(new ExplodingEvent("boom"));
        await Task.Delay(500);
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "After Failure"));

        // Assert — second event still arrives, proving the transport recovered
        await spy.WaitForCountAsync(1, TimeSpan.FromSeconds(10));
        spy.Events[0].Name.Should().Be("After Failure");
    }
}
