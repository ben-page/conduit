using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Resilience.Tests.Support;
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
    }

    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public async Task EmitRetryAttemptedForEachFailedAttempt()
    {
        // Arrange
        MessageContext.StartDispatch().AttachObserver(this.collector);
        var callCount = 0;

        var behavior = BuildBehavior(maxRetryAttempts: 2);

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
        retries[0].ExceptionType.Should().Be("System.TimeoutException");
        retries[1].Attempt.Should().Be(2);
    }

    [TestMethod]
    public async Task NotEmitRetryAttemptedWhenNoRetries()
    {
        // Arrange
        MessageContext.StartDispatch().AttachObserver(this.collector);

        var behavior = BuildBehavior(maxRetryAttempts: 2);

        // Act
        var result = await behavior.HandleAsync("test", () => Task.FromResult("success"), CancellationToken.None);

        // Assert
        result.Should().Be("success");
        this.collector.OfType<RetryAttempted>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task NotEmitRetryAttemptedForTheExhaustedAttempt()
    {
        // Arrange
        MessageContext.StartDispatch().AttachObserver(this.collector);

        var behavior = BuildBehavior(maxRetryAttempts: 1);

        // Act
        var act = () => behavior.HandleAsync("test",
            () => throw new InvalidOperationException("always fails"),
            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        var retries = this.collector.OfType<RetryAttempted>();
        retries.Should().ContainSingle(); // attempt 1 failed and was retried; attempt 2 failed and was not
        retries[0].Attempt.Should().Be(1);
    }

    [TestMethod]
    public async Task NotEmitRetryAttemptedForAPipelineTheBehaviorDidNotRun()
    {
        // Arrange -- the same registry also builds pipelines a handler runs itself (its HTTP client's, say).
        MessageContext.StartDispatch().AttachObserver(this.collector);
        var services = new ServiceCollection();
        new ConduitBuilder(services).AddResilience(resilience => resilience.AddDefaultPipeline(
            pipeline => pipeline.AddRetry(new RetryStrategyOptions { MaxRetryAttempts = 2, Delay = TimeSpan.Zero })));
        var pipeline = services.BuildServiceProvider().GetRequiredService<ResiliencePipelineProvider<string>>()
            .GetPipeline(ResilienceBehavior.DefaultPipelineKey);
        var callCount = 0;

        // Act
        await pipeline.ExecuteAsync(_ =>
        {
            callCount++;
            return callCount == 1 ? throw new TimeoutException("timeout") : ValueTask.CompletedTask;
        });

        // Assert -- Polly retried, but outside a Conduit dispatch's pipeline execution.
        callCount.Should().Be(2);
        this.collector.OfType<RetryAttempted>().Should().BeEmpty();
    }

    [TestMethod]
    public async Task KeepARetriedResultsValuesOutOfTheObservation()
    {
        // Arrange -- a typed pipeline for TestCommand that retries a result, not an exception.
        MessageContext.StartDispatch().AttachObserver(this.collector);
        var services = new ServiceCollection();
        new ConduitBuilder(services).AddResilience();
        services.AddResiliencePipeline<string, SecretResult>(ResilienceBehavior.TypePipelineKey(typeof(TestCommand)),
            pipeline => pipeline.AddRetry(new RetryStrategyOptions<SecretResult>
            {
                ShouldHandle = new PredicateBuilder<SecretResult>().HandleResult(result => result.Secret == "hunter2"),
                MaxRetryAttempts = 1,
                Delay = TimeSpan.Zero,
            }));
        var behavior = services.BuildServiceProvider()
            .GetRequiredService<ResilienceBehavior<TestCommand, SecretResult>>();
        var callCount = 0;

        // Act
        var result = await behavior.HandleAsync(new TestCommand("test"),
            () => Task.FromResult(new SecretResult(++callCount == 1 ? "hunter2" : "fine")),
            CancellationToken.None);

        // Assert -- the retry is observed, but a result is not captured: only its type is named.
        result.Secret.Should().Be("fine");
        var retry = this.collector.OfType<RetryAttempted>().Should().ContainSingle().Subject;
        retry.ErrorMessage.Should().NotContain("hunter2").And.Contain(typeof(SecretResult).FullName!);
        retry.ExceptionType.Should().BeEmpty();
    }

    /// <summary>The behavior as a host resolves it after <c>AddResilience</c> with a default retry pipeline.</summary>
    private static ResilienceBehavior<string, string> BuildBehavior(int maxRetryAttempts)
    {
        var services = new ServiceCollection();
        new ConduitBuilder(services).AddResilience(resilience => resilience.AddDefaultPipeline(
            pipeline => pipeline.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = maxRetryAttempts,
                Delay = TimeSpan.Zero,
            })));
        return services.BuildServiceProvider().GetRequiredService<ResilienceBehavior<string, string>>();
    }
}
