using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Resilience.Tests.Support;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Retry;

namespace Conduit.Resilience.Tests.Integration;

[TestClass]
public class ResilienceIntegrationShould
{
    [TestMethod]
    public async Task RetryCommandThroughFullPipeline()
    {
        // Arrange
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<CallTracker>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddResilience(resilience =>
                    resilience.AddDefaultPipeline(pipeline =>
                        pipeline.AddRetry(new RetryStrategyOptions
                        {
                            ShouldHandle = new PredicateBuilder().Handle<TimeoutException>(),
                            MaxRetryAttempts = 3,
                            Delay = TimeSpan.Zero,
                        })));
            });
        });

        var mediator = host.GetRequiredService<IMediator>();

        // Act — handler throws TimeoutException on the first call, succeeds on the second
        var result = await mediator.ExecuteAsync(new RetryCommand(FailCount: 1));

        // Assert
        result.Should().Be("success");

        var tracker = host.GetRequiredService<CallTracker>();
        tracker.CallCount.Should().Be(2);
    }

    [TestMethod]
    public async Task PassThroughWhenNoResiliencePipelineForType()
    {
        // Arrange — AddResilience with no pipelines configured; handler succeeds immediately
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<CallTracker>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.AddResilience();
            });
        });

        var mediator = host.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.ExecuteAsync(new RetryCommand(FailCount: 0));

        // Assert
        result.Should().Be("success");

        var tracker = host.GetRequiredService<CallTracker>();
        tracker.CallCount.Should().Be(1);
    }
}
