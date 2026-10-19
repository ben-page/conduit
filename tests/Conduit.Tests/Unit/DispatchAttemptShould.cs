using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class DispatchAttemptShould
{
    [TestMethod]
    public async Task OnKept_runs_for_a_successful_attempt()
    {
        // Arrange
        await using var provider = BuildProvider();
        var runs = 0;
        var runsBeforeTheDecision = -1;

        // Act
        await ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            _ =>
            {
                DispatchAttempt.Current!.OnKept(() => runs++);
                runsBeforeTheDecision = runs;
                return Task.FromResult(1);
            },
            CancellationToken.None);

        // Assert -- queued while undecided, run once the attempt succeeded
        runsBeforeTheDecision.Should().Be(0);
        runs.Should().Be(1);
    }

    [TestMethod]
    public async Task OnKept_runs_for_the_last_failed_attempt()
    {
        // Arrange
        await using var provider = BuildProvider(maxAttempts: 2);
        var kept = new List<int>();

        // Act
        var act = () => ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            _ =>
            {
                var attempt = DispatchAttempt.Current!;
                attempt.OnKept(() => kept.Add(attempt.Number));
                return Task.FromException<int>(new InvalidOperationException($"attempt {attempt.Number}"));
            },
            CancellationToken.None);

        // Assert -- the last attempt's failure is the answer, so its work runs; the first's was dropped
        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("attempt 2");
        kept.Should().Equal(2);
    }

    [TestMethod]
    public async Task OnKept_never_runs_for_an_abandoned_attempt()
    {
        // Arrange
        await using var provider = BuildProvider();
        var kept = new List<int>();

        // Act
        await ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            _ =>
            {
                var attempt = DispatchAttempt.Current!;
                attempt.OnKept(() => kept.Add(attempt.Number));
                return attempt.Number == 1
                    ? Task.FromException<int>(new InvalidOperationException("attempt 1"))
                    : Task.FromResult(attempt.Number);
            },
            CancellationToken.None);

        // Assert
        kept.Should().Equal(2);
    }

    [TestMethod]
    public async Task OnKept_after_the_decision_runs_at_once_if_kept_and_never_if_abandoned()
    {
        // Arrange
        await using var provider = BuildProvider();
        var attempts = new List<DispatchAttempt>();
        await ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            _ =>
            {
                attempts.Add(DispatchAttempt.Current!);
                return attempts.Count == 1
                    ? Task.FromException<int>(new InvalidOperationException("attempt 1"))
                    : Task.FromResult(attempts.Count);
            },
            CancellationToken.None);
        var abandonedRuns = 0;
        var keptRuns = 0;

        // Act
        attempts[0].OnKept(() => abandonedRuns++);
        attempts[1].OnKept(() => keptRuns++);

        // Assert
        abandonedRuns.Should().Be(0);
        keptRuns.Should().Be(1);
    }

    [TestMethod]
    public async Task A_throwing_kept_action_on_a_successful_attempt_reaches_the_caller_without_a_retry()
    {
        // Arrange -- the policy retries every failure it is asked about
        await using var provider = BuildProvider();
        var policy = (RecordingRetryPolicy)provider.GetRequiredService<IDispatchRetryPolicy>();
        var keptFailure = new InvalidOperationException("kept work failed");
        var calls = 0;

        // Act
        var act = () => ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            _ =>
            {
                calls++;
                DispatchAttempt.Current!.OnKept(() => throw keptFailure);
                return Task.FromResult(1);
            },
            CancellationToken.None);

        // Assert -- the attempt succeeded, so it is never offered to the policy and never run again
        var thrown = await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        thrown.Which.Should().BeSameAs(keptFailure);
        calls.Should().Be(1);
        policy.Failures.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Keep_runs_every_queued_action_when_one_throws()
    {
        // Arrange
        await using var provider = BuildProvider();
        var keptFailure = new InvalidOperationException("first kept action failed");
        var laterRuns = 0;

        // Act
        var act = () => ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            _ =>
            {
                DispatchAttempt.Current!.OnKept(() => throw keptFailure);
                DispatchAttempt.Current!.OnKept(() => laterRuns++);
                return Task.FromResult(1);
            },
            CancellationToken.None);

        // Assert -- the second action ran, and the one failure is rethrown as itself
        var thrown = await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        thrown.Which.Should().BeSameAs(keptFailure);
        laterRuns.Should().Be(1);
    }

    [TestMethod]
    public async Task Keep_aggregates_several_failing_actions()
    {
        // Arrange
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var attempt = new DispatchAttempt(1, 1, provider);
        var first = new InvalidOperationException("first");
        var second = new ArgumentException("second");
        attempt.OnKept(() => throw first);
        attempt.OnKept(() => throw second);

        // Act
        var act = attempt.Keep;

        // Assert -- both, in the order they were queued
        act.Should().ThrowExactly<AggregateException>()
            .Which.InnerExceptions.Should().Equal(first, second);
    }

    [TestMethod]
    public async Task The_first_decision_is_final()
    {
        // Arrange
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var kept = new DispatchAttempt(1, 2, provider);
        var abandoned = new DispatchAttempt(1, 2, provider);
        var keptRuns = 0;
        var abandonedRuns = 0;
        kept.OnKept(() => keptRuns++);
        abandoned.OnKept(() => abandonedRuns++);

        // Act -- a second, contrary decision on each
        kept.Keep();
        kept.Abandon();
        kept.Keep();
        abandoned.Abandon();
        abandoned.Keep();
        kept.OnKept(() => keptRuns++);
        abandoned.OnKept(() => abandonedRuns++);

        // Assert -- the kept attempt ran its queued work once and still runs new work; the abandoned one never does
        keptRuns.Should().Be(2);
        abandonedRuns.Should().Be(0);
    }

    [TestMethod]
    public async Task OnKept_runs_each_action_exactly_once_while_racing_the_decision()
    {
        // Arrange -- actions queued from many threads while the attempt is decided, some before and some after
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var attempt = new DispatchAttempt(1, 1, provider);
        const int actionCount = 2000;
        var runs = new int[actionCount];
        using var start = new ManualResetEventSlim();
        var registrations = Enumerable.Range(0, actionCount)
            .Select(index => Task.Run(() =>
            {
                start.Wait();
                attempt.OnKept(() => Interlocked.Increment(ref runs[index]));
            }))
            .ToArray();

        // Act
        start.Set();
        attempt.Keep();
        await Task.WhenAll(registrations);

        // Assert -- none lost between the queue and the decision, none run twice
        runs.Should().OnlyContain(count => count == 1);
    }

    [TestMethod]
    public async Task Current_is_null_after_the_loop()
    {
        // Arrange
        await using var provider = BuildProvider(maxAttempts: 2);
        DispatchAttempt? inside = null;

        // Act
        var act = () => ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            _ =>
            {
                inside = DispatchAttempt.Current;
                return Task.FromException<int>(new InvalidOperationException("fails"));
            },
            CancellationToken.None);
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();

        // Assert
        inside.Should().NotBeNull();
        DispatchAttempt.Current.Should().BeNull();
    }

    [TestMethod]
    public async Task Drops_its_scopes_provider_once_the_scope_is_disposed()
    {
        // Arrange
        await using var provider = BuildProvider();
        var attempts = new List<DispatchAttempt>();
        var servicesInside = new List<IServiceProvider?>();

        // Act
        await ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            attemptServices =>
            {
                attempts.Add(DispatchAttempt.Current!);
                servicesInside.Add(DispatchAttempt.Current!.Services);
                return attempts.Count == 1
                    ? Task.FromException<int>(new InvalidOperationException("attempt 1"))
                    : Task.FromResult(attempts.Count);
            },
            CancellationToken.None);

        // Assert -- each attempt held its scope's provider while it ran, and neither pins it now
        servicesInside.Should().OnlyContain(services => services != null);
        attempts.Should().HaveCount(2).And.OnlyContain(attempt => attempt.Services == null);
    }

    [TestMethod]
    public async Task BindDispatchAttempt_binds_only_the_same_provider()
    {
        // Arrange
        await using var provider = BuildProvider();
        await using var otherScope = provider.CreateAsyncScope();
        DispatchAttempt? attempt = null;
        MessageContext? sameProvider = null;
        MessageContext? otherProvider = null;
        var outside = MessageContext.StartDispatch();
        try
        {
            // Act
            ConduitRuntime.BindDispatchAttempt(outside, provider);
            await ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
                provider,
                attemptServices =>
                {
                    attempt = DispatchAttempt.Current;
                    sameProvider = MessageContext.StartDispatch();
                    ConduitRuntime.BindDispatchAttempt(sameProvider, attemptServices);
                    otherProvider = MessageContext.StartDispatch();
                    ConduitRuntime.BindDispatchAttempt(otherProvider, otherScope.ServiceProvider);
                    ConduitRuntime.BindDispatchAttempt(null, attemptServices);
                    return Task.FromResult(1);
                },
                CancellationToken.None);
        }
        finally
        {
            MessageContext.RestoreContext(null);
        }

        // Assert -- the attempt's own provider binds it; another provider, or no attempt at all, binds nothing
        attempt.Should().NotBeNull();
        sameProvider!.Attempt.Should().BeSameAs(attempt);
        otherProvider!.Attempt.Should().BeNull();
        outside.Attempt.Should().BeNull();
    }

    private static ServiceProvider BuildProvider(int maxAttempts = 3)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new ConduitBuilder(services).AddDispatchRetry<RecordingRetryPolicy>(options =>
        {
            options.MaxAttempts = maxAttempts;
            options.Delays.Clear();
            options.Delays.Add(new(TimeSpan.Zero, TimeSpan.Zero));
        });

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
