using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Registry;
using Polly.Retry;

namespace Conduit.Resilience.Tests.Unit;

[TestClass]
[DoNotParallelize]
public sealed class AuditRetryObservationsShould
{
    private ObservationCollector collector = null!;

    [TestInitialize]
    public void Setup()
    {
        this.collector = new ObservationCollector();
        MessageContext.Observer = this.collector;
    }

    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.Observer = null;
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public async Task EmitRetryAttemptedForEachFailedAttempt()
    {
        // Arrange
        MessageContext.StartDispatch();
        var callCount = 0;

        var services = new ServiceCollection();
        services.AddResiliencePipelineRegistry<string>();
        services.AddResiliencePipeline("Conduit.Default",
            builder => builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
            }));

        var provider = services.BuildServiceProvider();
        var pipelineProvider = provider.GetRequiredService<ResiliencePipelineProvider<string>>();
        var behavior = new ResilienceBehavior<string, string>(pipelineProvider);

        // Act
        var result = await behavior.HandleAsync("test", () =>
        {
            callCount++;
            if (callCount <= 2)
            {
                throw new TimeoutException("timeout");
            }

            return Task.FromResult("success");
        }, CancellationToken.None);

        // Assert
        result.Should().Be("success");
        var retries = this.collector.OfType<RetryAttempted>();
        retries.Should().HaveCount(2);
        retries[0].Attempt.Should().Be(1);
        retries[0].ExceptionType.Should().Be("TimeoutException");
        retries[1].Attempt.Should().Be(2);
    }

    [TestMethod]
    public async Task NotEmitRetryAttemptedWhenNoRetries()
    {
        // Arrange
        MessageContext.StartDispatch();

        var services = new ServiceCollection();
        services.AddResiliencePipelineRegistry<string>();
        services.AddResiliencePipeline("Conduit.Default",
            builder => builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
            }));

        var provider = services.BuildServiceProvider();
        var pipelineProvider = provider.GetRequiredService<ResiliencePipelineProvider<string>>();
        var behavior = new ResilienceBehavior<string, string>(pipelineProvider);

        // Act
        var result = await behavior.HandleAsync("test", () => Task.FromResult("success"), CancellationToken.None);

        // Assert
        result.Should().Be("success");
        this.collector.OfType<RetryAttempted>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task EmitRetryAttemptedEvenWhenRetriesExhausted()
    {
        // Arrange
        MessageContext.StartDispatch();

        var services = new ServiceCollection();
        services.AddResiliencePipelineRegistry<string>();
        services.AddResiliencePipeline("Conduit.Default",
            builder => builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.Zero,
            }));

        var provider = services.BuildServiceProvider();
        var pipelineProvider = provider.GetRequiredService<ResiliencePipelineProvider<string>>();
        var behavior = new ResilienceBehavior<string, string>(pipelineProvider);

        // Act
        var act = () => behavior.HandleAsync("test",
            () => throw new InvalidOperationException("always fails"),
            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        var retries = this.collector.OfType<RetryAttempted>();
        retries.Should().HaveCount(2); // attempt 1 (initial) + attempt 2 (retry) both failed
    }
}
