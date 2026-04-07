using AwesomeAssertions;
using Conduit.Exceptions;
using Conduit.Resilience;
using Conduit.Resilience.Tests.Support;
using Conduit.Runtime;
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
}
