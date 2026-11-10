using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Resilience;
using Conduit.Resilience.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Registry;
using Polly.Retry;

namespace Conduit.Resilience.Tests.Unit;

[TestClass]
public class ConduitResilienceBuilderShould
{
    [TestMethod]
    public void RegisterDefaultPipeline()
    {
        // Arrange
        var services = new ServiceCollection();
        var conduitBuilder = new ConduitBuilder(services);

        // Act
        conduitBuilder.AddResilience(resilience =>
            resilience.AddDefaultPipeline(builder =>
                builder.AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 1,
                    Delay = TimeSpan.Zero,
                })));

        // Assert
        var provider = services.BuildServiceProvider();
        var pipelineProvider = provider.GetRequiredService<ResiliencePipelineProvider<string>>();

        var act = () => pipelineProvider.GetPipeline(ResilienceBehavior.DefaultPipelineKey);
        act.Should().NotThrow();
    }

    [TestMethod]
    public void RegisterTypeSpecificPipeline()
    {
        // Arrange
        var services = new ServiceCollection();
        var conduitBuilder = new ConduitBuilder(services);
        var expectedKey = ResilienceBehavior.TypePipelineKey(typeof(TestCommand));

        // Act
        conduitBuilder.AddResilience(resilience =>
            resilience.AddPipelineForType<TestCommand>(builder =>
                builder.AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 1,
                    Delay = TimeSpan.Zero,
                })));

        // Assert
        var provider = services.BuildServiceProvider();
        var pipelineProvider = provider.GetRequiredService<ResiliencePipelineProvider<string>>();

        var act = () => pipelineProvider.GetPipeline(expectedKey);
        act.Should().NotThrow();
    }

    [TestMethod]
    public void RegisterResilienceBehaviorAsPipelineBehavior()
    {
        // Arrange
        var services = new ServiceCollection();
        var conduitBuilder = new ConduitBuilder(services);

        // Act
        conduitBuilder.AddResilience();

        // Assert
        conduitBuilder.Options.PipelineTypes[0].Should().Be(typeof(ResilienceBehavior<,>));
    }
}
