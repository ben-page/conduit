using AwesomeAssertions;
using Conduit.Middleware;
using Conduit.TestUtils;
using Conduit.Generated;
using Microsoft.Extensions.DependencyInjection;
using Conduit.Tests.Support;

namespace Conduit.Tests.Integration;

[TestClass]
public class PipelineBehaviorIntegrationShould
{
    [TestMethod]
    public async Task InvokePipelineBehaviorAroundCommand()
    {
        // Arrange
        var trackingBehavior = new TrackingBehavior();

        await using var host = TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                builder.Services.AddConduitHandlers();
                builder.Services.AddSingleton<IPipelineBehavior<CreateItemCommand, Guid>>(trackingBehavior);
            });

        var mediator = host.GetRequiredService<IMediator>();

        // Act
        var id = await mediator.ExecuteAsync(new CreateItemCommand("Pipeline Test"));

        // Assert
        id.Should().NotBeEmpty();
        trackingBehavior.CallLog.Should().BeEquivalentTo(["before", "after"]);
    }

    [TestMethod]
    public async Task InvokeEventPipelineBehaviorAroundPublish()
    {
        // Arrange
        var trackingBehavior = new TrackingEventBehavior();

        await using var host = TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                builder.Services.AddConduitHandlers();
                builder.Services.AddSingleton<IEventPipelineBehavior<ItemCreatedEvent>>(trackingBehavior);
            });

        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Event Test"));

        // Assert
        await spy.WaitForCountAsync(1);
        trackingBehavior.WasCalled.Should().BeTrue();
    }
}

file class TrackingBehavior : IPipelineBehavior<CreateItemCommand, Guid>
{
    public List<string> CallLog { get; } = [];

    public async Task<Guid> HandleAsync(
        CreateItemCommand request,
        Func<Task<Guid>> next,
        CancellationToken cancellationToken)
    {
        this.CallLog.Add("before");
        var result = await next();
        this.CallLog.Add("after");
        return result;
    }
}

file class TrackingEventBehavior : IEventPipelineBehavior<ItemCreatedEvent>
{
    public bool WasCalled { get; private set; }

    public async Task HandleAsync(
        ItemCreatedEvent @event,
        Func<Task> next,
        CancellationToken cancellationToken)
    {
        this.WasCalled = true;
        await next();
    }
}
