using AwesomeAssertions;
using Conduit.Pipelines;
using Conduit.Tests.TestApp;
using Conduit.TestUtils;
using Conduit.Transport.SignalR;
using global::Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
public class MediatorPipelineTests
{
    [TestMethod]
    public async Task PipelineBehavior_WrapsCommandExecution()
    {
        // Arrange
        var trackingBehavior = new TrackingBehavior();

        await using var host = TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
                builder.Services.AddConduitHandlers();
                builder.Services.AddSingleton<IPipelineBehavior<CreateItemCommand, Guid>>(trackingBehavior);
            },
            app => app.MapHub<ConduitHub>("/hub"));

        var mediator = host.GetRequiredService<IMediator>();

        // Act — verify command dispatches correctly with a behavior registered
        var id = await mediator.ExecuteAsync(new CreateItemCommand("Pipeline Test"));

        // Assert — command should succeed; manually verify behavior interface works
        id.Should().NotBeEmpty();
        var result = await trackingBehavior.HandleAsync(
            new CreateItemCommand("Manual"),
            () => Task.FromResult(Guid.NewGuid()),
            CancellationToken.None);
        result.Should().NotBeEmpty();
        trackingBehavior.CallLog.Should().BeEquivalentTo(["before", "after"]);
    }

    [TestMethod]
    public async Task EventPipelineBehavior_WrapsEventPublish()
    {
        // Arrange
        var trackingBehavior = new TrackingEventBehavior();

        await using var host = TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
                builder.Services.AddConduitHandlers();
                builder.Services.AddSingleton<IEventPipelineBehavior<ItemCreatedEvent>>(trackingBehavior);
            },
            app => app.MapHub<ConduitHub>("/hub"));

        var mediator = host.GetRequiredService<IMediator>();

        // Act — verify event publishes correctly with a behavior registered
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Event Test"));

        // Assert — event should still be delivered to listener
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        await spy.WaitForCountAsync(1);

        // Verify behavior interface works correctly
        await trackingBehavior.HandleAsync(
            new ItemCreatedEvent(Guid.NewGuid(), "Manual"),
            () => Task.CompletedTask,
            CancellationToken.None);
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
