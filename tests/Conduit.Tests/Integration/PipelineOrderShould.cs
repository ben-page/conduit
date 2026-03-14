using AwesomeAssertions;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
public class PipelineOrderShould
{
    [TestMethod]
    public async Task FormOnionLayersForCommands()
    {
        // Arrange
        await using var host = TestHost.Start(builder =>
        {
            builder.Services.AddSingleton<PipelineLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddPipeline(typeof(OuterPipeline<,>));
                conduit.AddPipeline(typeof(InnerPipeline<,>));
            });
        });
        var mediator = host.GetRequiredService<IMediator>();
        var log = host.GetRequiredService<PipelineLog>();

        // Act
        await mediator.ExecuteAsync(new EchoCommand("Test"));

        // Assert — onion: outer wraps inner wraps handler
        log.Entries.Should().BeEquivalentTo(
            ["Outer:before", "Inner:before", "Inner:after", "Outer:after"],
            options => options.WithStrictOrdering());
    }

    [TestMethod]
    public async Task FormOnionLayersForQueries()
    {
        // Arrange
        await using var host = TestHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddSingleton<PipelineLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddPipeline(typeof(OuterPipeline<,>));
                conduit.AddPipeline(typeof(InnerPipeline<,>));
            });
        });
        var mediator = host.GetRequiredService<IMediator>();
        var store = host.GetRequiredService<ItemStore>();
        var log = host.GetRequiredService<PipelineLog>();
        var id = Guid.NewGuid();
        store.Add(id, "Query");

        // Act
        await mediator.FetchAsync(new GetItemQuery(id));

        // Assert
        log.Entries.Should().BeEquivalentTo(
            ["Outer:before", "Inner:before", "Inner:after", "Outer:after"],
            options => options.WithStrictOrdering());
    }

    [TestMethod]
    public async Task FormOnionLayersForEvents()
    {
        // Arrange — same pipeline types wrap events too
        await using var host = TestHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddSingleton<PipelineLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddPipeline(typeof(OuterPipeline<,>));
                conduit.AddPipeline(typeof(InnerPipeline<,>));
            });
        });
        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        var log = host.GetRequiredService<PipelineLog>();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "Test"));

        // Assert
        await spy.WaitForCountAsync(1);
        log.Entries.Should().BeEquivalentTo(
            ["Outer:before", "Inner:before", "Inner:after", "Outer:after"],
            options => options.WithStrictOrdering());
    }
}
