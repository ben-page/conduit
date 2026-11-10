using System.Text.Json;
using AwesomeAssertions;
using Conduit.Exceptions;
using Conduit.Resilience;
using Conduit.Runtime;
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
    public async Task TransientErrorsDoesNotHandleInvalidOperationException()
    {
        // Arrange / Act
        var attempts = await ExecuteWithRetryAsync(
            ConduitPredicates.TransientErrors<object>(),
            () => throw new InvalidOperationException());

        // Assert
        attempts.Should().Be(1);
    }

    [TestMethod]
    [DataRow("NotFound", 404)]
    [DataRow("Unauthorized", 401)]
    [DataRow("Forbidden", 403)]
    [DataRow("Conflict", 409)]
    [DataRow("PreconditionFailed", 412)]
    [DataRow("BadRequest", 400)]
    [DataRow("ContractMismatch", 400)]
    public async Task NotRetryARemoteRefusal(string code, int status)
    {
        // Arrange
        var reply = RemoteReply(code, status);

        // Act
        var attempts = await ExecuteWithRetryAsync(
            ConduitPredicates.TransientErrors<object>(),
            () => Task.FromResult(ConduitRuntime.UnwrapReply(reply)));

        // Assert
        attempts.Should().Be(1);
    }

    [TestMethod]
    public async Task RetryARemoteInternalError()
    {
        // Arrange
        var reply = RemoteReply("InternalError", 500);

        // Act
        var attempts = await ExecuteWithRetryAsync(
            ConduitPredicates.TransientErrors<object>(),
            () => Task.FromResult(ConduitRuntime.UnwrapReply(reply)));

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

    /// <summary>A remote endpoint's error reply as it arrives on the wire, before <see cref="ConduitRuntime.UnwrapReply"/> reads it.</summary>
    private static DispatchResult<JsonElement?> RemoteReply(string code, int status) =>
        JsonSerializer.Deserialize<DispatchResult<JsonElement?>>(
            $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"refused\",\"status\":{status}}}}}",
            ConduitRuntime.JsonOptions)!;
}
