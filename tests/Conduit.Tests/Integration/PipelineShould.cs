using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Middleware;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
public class PipelineShould
{
    [TestMethod]
    public async Task WrapCommandExecution()
    {
        // Arrange
        await using var host = TestHost.Start(builder =>
        {
            builder.Services.AddSingleton<PipelineLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddPipeline(typeof(LoggingPipeline<,>));
            });
        });
        var mediator = host.GetRequiredService<IMediator>();
        var log = host.GetRequiredService<PipelineLog>();

        // Act
        var result = await mediator.ExecuteAsync(new EchoCommand("Test"));

        // Assert
        result.Should().Be("Test");
        log.Entries.Should().BeEquivalentTo(
            ["Pipeline:before", "Pipeline:after"],
            options => options.WithStrictOrdering());
    }

    [TestMethod]
    public async Task WrapVoidCommandExecution()
    {
        // Arrange
        await using var host = TestHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddSingleton<PipelineLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddPipeline(typeof(LoggingPipeline<,>));
            });
        });
        var mediator = host.GetRequiredService<IMediator>();
        var store = host.GetRequiredService<ItemStore>();
        var log = host.GetRequiredService<PipelineLog>();
        var id = Guid.NewGuid();
        store.Add(id, "To Delete");

        // Act
        await mediator.ExecuteAsync(new DeleteItemCommand(id));

        // Assert
        store.Get(id).Should().BeNull();
        log.Entries.Should().BeEquivalentTo(
            ["Pipeline:before", "Pipeline:after"],
            options => options.WithStrictOrdering());
    }

    [TestMethod]
    public async Task WrapQueryExecution()
    {
        // Arrange
        await using var host = TestHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddSingleton<PipelineLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddPipeline(typeof(LoggingPipeline<,>));
            });
        });
        var mediator = host.GetRequiredService<IMediator>();
        var store = host.GetRequiredService<ItemStore>();
        var log = host.GetRequiredService<PipelineLog>();
        var id = Guid.NewGuid();
        store.Add(id, "Query Test");

        // Act
        var result = await mediator.FetchAsync(new GetItemQuery(id));

        // Assert
        result.Should().Be("Query Test");
        log.Entries.Should().BeEquivalentTo(
            ["Pipeline:before", "Pipeline:after"],
            options => options.WithStrictOrdering());
    }

    [TestMethod]
    public async Task WrapEventPublishing()
    {
        // Arrange — same open generic pipeline wraps events too
        await using var host = TestHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddSingleton<PipelineLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddPipeline(typeof(LoggingPipeline<,>));
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
            ["Pipeline:before", "Pipeline:after"],
            options => options.WithStrictOrdering());
    }
}

file class PipelineLog
{
    public List<string> Entries { get; } = [];
}

file class LoggingPipeline<TRequest, TResult>(PipelineLog log) : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        log.Entries.Add("Pipeline:before");
        var result = await next();
        log.Entries.Add("Pipeline:after");
        return result;
    }
}
