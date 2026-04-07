using AwesomeAssertions;
using Conduit.Exceptions;
using Conduit.Resilience;
using Polly;
using Polly.Retry;

namespace Conduit.Resilience.Tests.Unit;

[TestClass]
public class ConduitPredicatesShould
{
    private static async Task<int> ExecuteWithRetryAsync(PredicateBuilder<object> predicate, Func<Task> action)
    {
        var attempts = 0;
        var pipeline = new ResiliencePipelineBuilder<object>()
            .AddRetry(new RetryStrategyOptions<object>
            {
                ShouldHandle = predicate,
                MaxRetryAttempts = 1,
                Delay = TimeSpan.Zero,
            })
            .Build();

        try
        {
            await pipeline.ExecuteAsync<object>(async _ =>
            {
                attempts++;
                await action();
                return default!;
            });
        }
        catch
        {
            // ignore — we only care about attempt count
        }

        return attempts;
    }

    [TestMethod]
    public async Task TransientErrorsHandlesTimeoutException()
    {
        // Arrange / Act
        var attempts = await ExecuteWithRetryAsync(
            ConduitPredicates.TransientErrors<object>(),
            () => throw new TimeoutException());

        // Assert
        attempts.Should().Be(2);
    }

    [TestMethod]
    public async Task TransientErrorsHandlesInvalidOperationException()
    {
        // Arrange / Act
        var attempts = await ExecuteWithRetryAsync(
            ConduitPredicates.TransientErrors<object>(),
            () => throw new InvalidOperationException());

        // Assert
        attempts.Should().Be(2);
    }

    [TestMethod]
    public async Task TransientErrorsDoesNotHandleNotFoundException()
    {
        // Arrange / Act
        var attempts = await ExecuteWithRetryAsync(
            ConduitPredicates.TransientErrors<object>(),
            () => throw new NotFoundException("not found"));

        // Assert
        attempts.Should().Be(1);
    }

    [TestMethod]
    public async Task TransientErrorsDoesNotHandleUnauthorizedException()
    {
        // Arrange / Act
        var attempts = await ExecuteWithRetryAsync(
            ConduitPredicates.TransientErrors<object>(),
            () => throw new UnauthorizedException("unauthorized"));

        // Assert
        attempts.Should().Be(1);
    }

    [TestMethod]
    public async Task TransientErrorsDoesNotHandleConflictException()
    {
        // Arrange / Act
        var attempts = await ExecuteWithRetryAsync(
            ConduitPredicates.TransientErrors<object>(),
            () => throw new ConflictException("conflict"));

        // Assert
        attempts.Should().Be(1);
    }

    [TestMethod]
    public async Task AllErrorsHandlesAnyException()
    {
        // Arrange / Act
        var attempts = await ExecuteWithRetryAsync(
            ConduitPredicates.AllErrors<object>(),
            () => throw new InvalidOperationException("unhandled application error"));

        // Assert
        attempts.Should().Be(2);
    }

    [TestMethod]
    public async Task AllErrorsDoesNotHandleOperationCanceledException()
    {
        // Arrange / Act
        var attempts = await ExecuteWithRetryAsync(
            ConduitPredicates.AllErrors<object>(),
            () => throw new OperationCanceledException());

        // Assert
        attempts.Should().Be(1);
    }
}
