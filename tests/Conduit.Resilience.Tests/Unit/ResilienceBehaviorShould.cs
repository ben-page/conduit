using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Exceptions;
using Conduit.Resilience;
using Conduit.Resilience.Tests.Support;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Registry;
using Polly.Retry;

namespace Conduit.Resilience.Tests.Unit;

[TestClass]
public class ResilienceBehaviorShould
{
    private static ResilienceBehavior<TestCommand, string> BuildBehavior(
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddResiliencePipelineRegistry<string>();
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();
        var pipelineProvider = provider.GetRequiredService<ResiliencePipelineProvider<string>>();
        return new ResilienceBehavior<TestCommand, string>(pipelineProvider);
    }

    private static ResilienceBehavior<TestCommand, string> BuildBehaviorWithTypedPipeline(
        string key,
        Action<ResiliencePipelineBuilder<string>> configurePipeline)
    {
        var services = new ServiceCollection();
        services.AddResiliencePipeline<string, string>(key, configurePipeline);
        var provider = services.BuildServiceProvider();
        var pipelineProvider = provider.GetRequiredService<ResiliencePipelineProvider<string>>();
        return new ResilienceBehavior<TestCommand, string>(pipelineProvider);
    }

    [TestMethod]
    public async Task RetryOnTransientError()
    {
        // Arrange
        var handler = new FailingHandler(
            new TimeoutException(),
            new TimeoutException());

        var typeKey = typeof(TestCommand).FullName!;
        var behavior = BuildBehaviorWithTypedPipeline(typeKey, builder =>
            builder.AddRetry(new RetryStrategyOptions<string>
            {
                ShouldHandle = ConduitPredicates.TransientErrors<string>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
            }));

        // Act
        var result = await behavior.HandleAsync(
            new TestCommand("test"),
            handler.HandleAsync,
            CancellationToken.None);

        // Assert
        result.Should().Be("success");
        handler.CallCount.Should().Be(3);
    }

    [TestMethod]
    public async Task NotRetryOnNotFoundException()
    {
        // Arrange
        var handler = new FailingHandler(new NotFoundException("not found"));

        var typeKey = typeof(TestCommand).FullName!;
        var behavior = BuildBehaviorWithTypedPipeline(typeKey, builder =>
            builder.AddRetry(new RetryStrategyOptions<string>
            {
                ShouldHandle = ConduitPredicates.TransientErrors<string>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
            }));

        // Act
        var act = () => behavior.HandleAsync(
            new TestCommand("test"),
            handler.HandleAsync,
            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<NotFoundException>();
        handler.CallCount.Should().Be(1);
    }

    [TestMethod]
    public async Task NotRetryOnUnauthorizedException()
    {
        // Arrange
        var handler = new FailingHandler(new UnauthorizedException("unauthorized"));

        var typeKey = typeof(TestCommand).FullName!;
        var behavior = BuildBehaviorWithTypedPipeline(typeKey, builder =>
            builder.AddRetry(new RetryStrategyOptions<string>
            {
                ShouldHandle = ConduitPredicates.TransientErrors<string>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
            }));

        // Act
        var act = () => behavior.HandleAsync(
            new TestCommand("test"),
            handler.HandleAsync,
            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<UnauthorizedException>();
        handler.CallCount.Should().Be(1);
    }

    [TestMethod]
    public async Task PassThroughWhenNoResiliencePipelineConfigured()
    {
        // Arrange
        var handler = new FailingHandler();
        var behavior = BuildBehavior();

        // Act
        var result = await behavior.HandleAsync(
            new TestCommand("test"),
            handler.HandleAsync,
            CancellationToken.None);

        // Assert
        result.Should().Be("success");
        handler.CallCount.Should().Be(1);
    }

    [TestMethod]
    public async Task EnrichResilienceContextWithMessageContext()
    {
        // Arrange
        var context = MessageContext.StartDispatch();
        string? capturedMessageId = null;
        var callCount = 0;

        var handler = new FailingHandler(new TimeoutException());

        var typeKey = typeof(TestCommand).FullName!;
        var behavior = BuildBehaviorWithTypedPipeline(typeKey, builder =>
            builder.AddRetry(new RetryStrategyOptions<string>
            {
                ShouldHandle = ConduitPredicates.TransientErrors<string>(),
                MaxRetryAttempts = 1,
                Delay = TimeSpan.Zero,
                OnRetry = args =>
                {
                    args.Context.Properties.TryGetValue(
                        ResilienceBehavior.MessageIdKey,
                        out capturedMessageId);
                    callCount++;
                    return ValueTask.CompletedTask;
                },
            }));

        try
        {
            // Act
            await behavior.HandleAsync(
                new TestCommand("test"),
                handler.HandleAsync,
                CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // expected — retry exhausted
        }

        // Assert
        callCount.Should().Be(1);
        capturedMessageId.Should().Be(context.MessageId);

        // Cleanup
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public async Task UseDefaultPipelineKeyWhenTypeKeyNotFound()
    {
        // Arrange
        var handler = new FailingHandler(
            new TimeoutException());

        var behavior = BuildBehaviorWithTypedPipeline(ResilienceBehavior.DefaultPipelineKey, builder =>
            builder.AddRetry(new RetryStrategyOptions<string>
            {
                ShouldHandle = ConduitPredicates.TransientErrors<string>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
            }));

        // Act
        var result = await behavior.HandleAsync(
            new TestCommand("test"),
            handler.HandleAsync,
            CancellationToken.None);

        // Assert
        result.Should().Be("success");
        handler.CallCount.Should().Be(2);
    }

    [TestMethod]
    public async Task ResolveANamespacePipeline()
    {
        // Arrange -- TestCommand is Conduit.Resilience.Tests.Support.TestCommand. Each pipeline retries once and
        // records its own name, so the one that ran is the one that was resolved.
        var resolved = new List<string>();
        var behavior = BuildConduitBehavior(resilience => resilience
            .AddDefaultPipeline(pipeline => RetryOnceRecording(pipeline, "default", resolved))
            .AddPipelineForNamespace("Conduit", pipeline => RetryOnceRecording(pipeline, "Conduit", resolved))
            .AddPipelineForNamespace("Conduit.Resilience.Tests",
                pipeline => RetryOnceRecording(pipeline, "Conduit.Resilience.Tests", resolved))
            .AddPipelineForNamespace("Conduit.Resilience.Tests.Supp",
                pipeline => RetryOnceRecording(pipeline, "Conduit.Resilience.Tests.Supp", resolved)));
        var handler = new FailingHandler(new TimeoutException());

        // Act
        var result = await behavior.HandleAsync(new TestCommand("test"), handler.HandleAsync, CancellationToken.None);

        // Assert -- the longest registered prefix on whole namespace segments wins over a shorter one, over a
        // prefix that only matches part of a segment, and over the default.
        result.Should().Be("success");
        resolved.Should().Equal("Conduit.Resilience.Tests");
    }

    [TestMethod]
    public async Task ObserveOnlyRealRetries()
    {
        // Arrange -- retries TimeoutException only, twice, 5 ms apart.
        var collector = new ObservationCollector();
        var behavior = BuildConduitBehavior(resilience => resilience
            .AddDefaultPipeline(pipeline => pipeline.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<TimeoutException>(),
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(5),
                BackoffType = Polly.DelayBackoffType.Constant,
                UseJitter = false,
            })));

        try
        {
            MessageContext.StartDispatch().AttachObserver(collector);

            // Act -- three timeouts exhaust the retries; then a refusal the predicate does not handle.
            var exhausted = () => behavior.HandleAsync(new TestCommand("test"),
                new FailingHandler(new TimeoutException("first"), new TimeoutException("second"), new TimeoutException("third"))
                    .HandleAsync,
                CancellationToken.None);
            await exhausted.Should().ThrowAsync<TimeoutException>();
            var afterExhausted = collector.OfType<RetryAttempted>();

            var unhandled = () => behavior.HandleAsync(new TestCommand("test"),
                new FailingHandler(new ConflictException("conflict")).HandleAsync,
                CancellationToken.None);
            await unhandled.Should().ThrowAsync<ConflictException>();

            // Assert -- one observation per retry Polly made, with its delay; none for the third timeout, which was
            // not retried, and none for the refusal.
            afterExhausted.Select(retry => (retry.Attempt, retry.ErrorMessage, retry.ExceptionType, retry.DelayMs))
                .Should().Equal(
                    (1, "first", "System.TimeoutException", 5),
                    (2, "second", "System.TimeoutException", 5));
            collector.OfType<RetryAttempted>().Should().HaveCount(2);
        }
        finally
        {
            MessageContext.RestoreContext(null);
        }
    }

    /// <summary>The behavior as a host resolves it after <c>AddResilience</c>.</summary>
    private static ResilienceBehavior<TestCommand, string> BuildConduitBehavior(Action<ConduitResilienceBuilder> configure)
    {
        var services = new ServiceCollection();
        new ConduitBuilder(services).AddResilience(configure);
        return services.BuildServiceProvider().GetRequiredService<ResilienceBehavior<TestCommand, string>>();
    }

    private static void RetryOnceRecording(ResiliencePipelineBuilder pipeline, string name, List<string> resolved) =>
        pipeline.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 1,
            Delay = TimeSpan.Zero,
            OnRetry = _ =>
            {
                resolved.Add(name);
                return ValueTask.CompletedTask;
            },
        });
}
