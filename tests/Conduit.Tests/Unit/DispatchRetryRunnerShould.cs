using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.Tests.Support;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class DispatchRetryRunnerShould
{
    [TestMethod]
    public async Task Runs_on_the_callers_provider_without_a_policy()
    {
        // Arrange
        await using var provider = BuildProvider(withPolicy: false);
        await using var callerScope = provider.CreateAsyncScope();
        var callerProbe = callerScope.ServiceProvider.GetRequiredService<AttemptProbe>();
        IServiceProvider? seen = null;
        DispatchAttempt? attemptSeen = null;

        // Act
        var probeSeen = await ConduitRuntime.DispatchAttemptsAsync<RetryRequest, AttemptProbe>(
            callerScope.ServiceProvider,
            attemptServices =>
            {
                seen = attemptServices;
                attemptSeen = DispatchAttempt.Current;
                return Task.FromResult(attemptServices.GetRequiredService<AttemptProbe>());
            },
            CancellationToken.None);

        // Assert -- the very provider, so the caller's scoped probe, and no attempt
        seen.Should().BeSameAs(callerScope.ServiceProvider);
        probeSeen.Should().BeSameAs(callerProbe);
        attemptSeen.Should().BeNull();
        callerProbe.Disposed.Should().BeFalse("no scope was created and disposed around the call");
    }

    [TestMethod]
    public async Task An_injected_IServiceProvider_is_its_scopes_ServiceProvider()
    {
        // Arrange
        await using var provider = BuildProvider(withPolicy: false);
        await using var scope = provider.CreateAsyncScope();

        // Act
        var capture = scope.ServiceProvider.GetRequiredService<ServiceProviderCapture>();

        // Assert -- the premise the nested check rests on: a handler's injected provider IS the attempt's
        ReferenceEquals(capture.Services, scope.ServiceProvider).Should().BeTrue();
    }

    [TestMethod]
    public async Task Runs_inline_when_nested_in_the_attempts_own_scope()
    {
        // Arrange
        await using var provider = BuildProvider();
        var outerCalls = 0;
        var innerCalls = 0;
        IServiceProvider? outerServices = null;
        IServiceProvider? innerServices = null;
        DispatchAttempt? outerAttempt = null;
        DispatchAttempt? innerAttempt = null;

        // Act
        await ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            async attemptServices =>
            {
                outerCalls++;
                outerServices = attemptServices;
                outerAttempt = DispatchAttempt.Current;
                return await ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
                    attemptServices.GetRequiredService<ServiceProviderCapture>().Services,
                    innerAttemptServices =>
                    {
                        innerCalls++;
                        innerServices = innerAttemptServices;
                        innerAttempt = DispatchAttempt.Current;
                        return Task.FromResult(1);
                    },
                    CancellationToken.None);
            },
            CancellationToken.None);

        // Assert -- one call each; the inner ran on the attempt's own provider, under the outer attempt
        outerCalls.Should().Be(1);
        innerCalls.Should().Be(1);
        innerServices.Should().BeSameAs(outerServices);
        innerAttempt.Should().BeSameAs(outerAttempt);
    }

    [TestMethod]
    public async Task A_dispatch_from_another_provider_inside_an_attempt_gets_its_own_attempts()
    {
        // Arrange
        await using var provider = BuildProvider();
        IServiceProvider? outerServices = null;
        DispatchAttempt? outerAttempt = null;
        DispatchAttempt? currentAfterInner = null;
        IServiceProvider? freshServices = null;
        var innerServices = new List<IServiceProvider>();
        var innerAttempts = new List<DispatchAttempt?>();

        // Act
        await ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            async attemptServices =>
            {
                outerServices = attemptServices;
                outerAttempt = DispatchAttempt.Current;
                await using var fresh = attemptServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
                freshServices = fresh.ServiceProvider;
                var result = await ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
                    fresh.ServiceProvider,
                    innerAttemptServices =>
                    {
                        innerServices.Add(innerAttemptServices);
                        innerAttempts.Add(DispatchAttempt.Current);
                        return innerServices.Count == 1
                            ? Task.FromException<int>(new InvalidOperationException("inner attempt 1"))
                            : Task.FromResult(2);
                    },
                    CancellationToken.None);
                currentAfterInner = DispatchAttempt.Current;
                return result;
            },
            CancellationToken.None);

        // Assert -- its own scope per attempt (neither the fresh provider nor the outer attempt's), its own retry,
        // and the outer attempt current again once it returned
        innerServices.Should().HaveCount(2);
        innerServices.Should().NotContain(freshServices!);
        innerServices.Should().NotContain(outerServices!);
        innerServices[0].Should().NotBeSameAs(innerServices[1]);
        innerAttempts.Select(attempt => attempt!.Number).Should().Equal(1, 2);
        innerAttempts.Should().NotContain(outerAttempt);
        currentAfterInner.Should().BeSameAs(outerAttempt);
    }

    [TestMethod]
    public async Task Runs_inline_when_the_policy_does_not_cover_the_type()
    {
        // Arrange
        await using var provider = BuildProvider();
        var policy = GetPolicy(provider);
        policy.CoversType = type => type != typeof(RetryOtherRequest);
        await using var callerScope = provider.CreateAsyncScope();
        IServiceProvider? seen = null;
        var calls = 0;

        // Act
        var act = () => ConduitRuntime.DispatchAttemptsAsync<RetryOtherRequest, int>(
            callerScope.ServiceProvider,
            attemptServices =>
            {
                calls++;
                seen = attemptServices;
                return Task.FromException<int>(new InvalidOperationException("not covered"));
            },
            CancellationToken.None);

        // Assert -- the caller's provider, one attempt, and the policy never asked
        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("not covered");
        seen.Should().BeSameAs(callerScope.ServiceProvider);
        calls.Should().Be(1);
        policy.Failures.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Each_attempt_gets_a_new_scope_never_the_callers()
    {
        // Arrange
        await using var provider = BuildProvider();
        await using var callerScope = provider.CreateAsyncScope();
        var callerProbe = callerScope.ServiceProvider.GetRequiredService<AttemptProbe>();
        var probes = new List<AttemptProbe>();

        // Act
        await ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            callerScope.ServiceProvider,
            attemptServices =>
            {
                probes.Add(attemptServices.GetRequiredService<AttemptProbe>());
                return probes.Count < 3
                    ? Task.FromException<int>(new InvalidOperationException($"attempt {probes.Count}"))
                    : Task.FromResult(3);
            },
            CancellationToken.None);

        // Assert -- three probes, three ids, none the caller's, every attempt scope disposed
        probes.Select(probe => probe.Id).Distinct().Should().HaveCount(3);
        probes.Select(probe => probe.Id).Should().NotContain(callerProbe.Id);
        probes.Should().OnlyContain(probe => probe.Disposed);
        callerProbe.Disposed.Should().BeFalse();
    }

    [TestMethod]
    public async Task Retries_while_the_policy_gives_a_reason_then_rethrows_the_last_exception()
    {
        // Arrange
        await using var provider = BuildProvider();
        var policy = GetPolicy(provider);
        var thrown = new List<Exception>();

        // Act
        var act = () => ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            _ =>
            {
                var exception = new InvalidOperationException($"attempt {thrown.Count + 1}");
                thrown.Add(exception);
                return Task.FromException<int>(exception);
            },
            CancellationToken.None);

        // Assert -- three attempts (the default), each offered to the policy, the last one's exception rethrown
        var failure = await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        failure.Which.Should().BeSameAs(thrown[2]);
        thrown.Should().HaveCount(3);
        policy.Failures.Select(asked => (asked.Attempt, asked.MaxAttempts)).Should().Equal((1, 3), (2, 3), (3, 3));
        policy.Failures.Select(asked => asked.Exception).Should().Equal(thrown);
        policy.Failures.Should().OnlyContain(asked => asked.RequestType == typeof(RetryRequest));
    }

    [TestMethod]
    public async Task A_null_reason_rethrows_the_first_exception_at_once()
    {
        // Arrange
        await using var provider = BuildProvider();
        var policy = GetPolicy(provider);
        policy.Reason = _ => null;
        var exception = new InvalidOperationException("not retryable");
        var calls = 0;

        // Act
        var act = () => ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            _ =>
            {
                calls++;
                return Task.FromException<int>(exception);
            },
            CancellationToken.None);

        // Assert
        var failure = await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        failure.Which.Should().BeSameAs(exception);
        calls.Should().Be(1);
        policy.Failures.Should().ContainSingle();
    }

    [TestMethod]
    public async Task The_policy_reads_the_failed_attempts_services_before_they_are_disposed()
    {
        // Arrange
        await using var provider = BuildProvider(options => options.MaxAttempts = 2);
        var policy = GetPolicy(provider);
        var probes = new List<AttemptProbe>();

        // Act
        var act = () => ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            attemptServices =>
            {
                probes.Add(attemptServices.GetRequiredService<AttemptProbe>());
                return Task.FromException<int>(new InvalidOperationException("fails"));
            },
            CancellationToken.None);

        // Assert -- the policy resolved each attempt's own probe, still live; disposed only afterwards
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        policy.ProbesSeen.Should().Equal(probes.Select(probe => (probe.Id, false)));
        probes.Should().OnlyContain(probe => probe.Disposed);
    }

    [TestMethod]
    public async Task A_throwing_policy_is_logged_and_stops_the_loop()
    {
        // Arrange
        var logger = new RecordingLogger<DispatchRetryRunner>();
        await using var provider = BuildProvider(logger: logger);
        var policy = GetPolicy(provider);
        var policyFailure = new InvalidOperationException("policy broke");
        policy.Reason = _ => throw policyFailure;
        var exception = new ArgumentException("the attempt's own failure");
        var probes = new List<AttemptProbe>();

        // Act
        var act = () => ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            attemptServices =>
            {
                probes.Add(attemptServices.GetRequiredService<AttemptProbe>());
                return Task.FromException<int>(exception);
            },
            CancellationToken.None);

        // Assert -- the attempt's exception, not the policy's; one attempt; a Warning carrying the policy's failure
        var failure = await act.Should().ThrowExactlyAsync<ArgumentException>();
        failure.Which.Should().BeSameAs(exception);
        probes.Should().ContainSingle().Which.Disposed.Should().BeTrue();
        var entry = logger.Entries.Should().ContainSingle(logged => logged.EventId.Id == 19).Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.EventId.Name.Should().Be("DispatchRetryPolicyFailed");
        entry.Exception.Should().BeSameAs(policyFailure);
    }

    [TestMethod]
    public async Task Ignores_a_change_to_its_options_after_it_is_built()
    {
        // Arrange -- the runner is built by a first dispatch; then the resolvable options are emptied
        await using var provider = BuildProvider();
        await ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider, _ => Task.FromResult(1), CancellationToken.None);
        var options = provider.GetRequiredService<DispatchRetryOptions>();
        options.MaxAttempts = 1;
        options.Delays.Clear();
        var thrown = new List<Exception>();

        // Act
        var act = () => ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            _ =>
            {
                var exception = new InvalidOperationException($"attempt {thrown.Count + 1}");
                thrown.Add(exception);
                return Task.FromException<int>(exception);
            },
            CancellationToken.None);

        // Assert -- still three attempts with the validated delays, and the last attempt's own exception
        var failure = await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        failure.Which.Should().BeSameAs(thrown[^1]);
        thrown.Should().HaveCount(3);
    }

    [TestMethod]
    public async Task Waits_the_configured_delay_before_each_retry()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        await using var provider = BuildProvider(options =>
        {
            options.Delays.Clear();
            options.Delays.Add(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
            options.Delays.Add(new(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)));
            options.TimeProvider = clock;
        });
        var attempts = 0;

        // Act -- each attempt fails synchronously, so the loop is already waiting when the call returns
        var dispatch = ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            _ =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                return attempt < 3
                    ? Task.FromException<int>(new InvalidOperationException($"attempt {attempt}"))
                    : Task.FromResult(attempt);
            },
            CancellationToken.None);

        // Assert -- attempt 2 only after 5 s, attempt 3 only after 7 more (Delays[1], not Delays[0] again)
        Volatile.Read(ref attempts).Should().Be(1);
        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(1));
        Volatile.Read(ref attempts).Should().Be(1);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Volatile.Read(ref attempts).Should().Be(2);
        clock.Advance(TimeSpan.FromSeconds(7) - TimeSpan.FromMilliseconds(1));
        Volatile.Read(ref attempts).Should().Be(2);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        (await dispatch.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(3);
    }

    [TestMethod]
    public async Task A_cancelled_wait_stops_without_another_attempt()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        await using var provider = BuildProvider(options =>
        {
            options.Delays.Clear();
            options.Delays.Add(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
            options.TimeProvider = clock;
        });
        using var cancellation = new CancellationTokenSource();
        var probes = new List<AttemptProbe>();
        var dispatch = ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            attemptServices =>
            {
                probes.Add(attemptServices.GetRequiredService<AttemptProbe>());
                return Task.FromException<int>(new InvalidOperationException("fails"));
            },
            cancellation.Token);

        // Act
        await cancellation.CancelAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        var act = () => dispatch.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert -- the wait's cancellation, one attempt, its scope already disposed
        await act.Should().ThrowAsync<OperationCanceledException>();
        probes.Should().ContainSingle().Which.Disposed.Should().BeTrue();
    }

    [TestMethod]
    public async Task A_cancelled_attempt_is_not_offered_to_the_policy()
    {
        // Arrange
        await using var provider = BuildProvider();
        var policy = GetPolicy(provider);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;

        // Act
        var act = () => ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
            provider,
            async _ =>
            {
                calls++;
                await cancellation.CancelAsync();
                cancellation.Token.ThrowIfCancellationRequested();
                return 1;
            },
            cancellation.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(1);
        policy.Failures.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Counts_retried_and_exhausted_with_the_reason()
    {
        // Arrange -- the meter is static and shared by parallel tests: keep only this test's own type
        var ownType = typeof(RetryCountedRequest).FullName;
        var measurements = new ConcurrentQueue<(long Value, string? Reason, string? Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Conduit" && instrument.Name == "conduit.dispatch.retries")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var tagValues = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                tagValues[tag.Key] = tag.Value;
            }

            if ((tagValues.GetValueOrDefault("conduit.message.type") as string) == ownType)
            {
                measurements.Enqueue((
                    value,
                    tagValues.GetValueOrDefault("conduit.retry.reason") as string,
                    tagValues.GetValueOrDefault("conduit.retry.outcome") as string));
            }
        });
        listener.Start();
        await using var provider = BuildProvider();
        GetPolicy(provider).Reason = _ => "deadlock";

        // Act
        var act = () => ConduitRuntime.DispatchAttemptsAsync<RetryCountedRequest, int>(
            provider,
            _ => Task.FromException<int>(new InvalidOperationException("always")),
            CancellationToken.None);

        // Assert -- two retries, then the third attempt's reason with no attempts left
        await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        measurements.Should().Equal(
            (1L, "deadlock", "retried"),
            (1L, "deadlock", "retried"),
            (1L, "deadlock", "exhausted"));
    }

    [TestMethod]
    public async Task Observes_RetryAttempted_on_the_dispatchs_context()
    {
        // Arrange
        await using var provider = BuildProvider();
        var collector = new ObservationCollector();
        var context = MessageContext.StartDispatch();
        context.AttachObserver(collector);
        var calls = 0;
        try
        {
            // Act
            await ConduitRuntime.DispatchAttemptsAsync<RetryRequest, int>(
                provider,
                _ => ++calls == 1
                    ? Task.FromException<int>(new InvalidOperationException("attempt 1 failed"))
                    : Task.FromResult(calls),
                CancellationToken.None);
        }
        finally
        {
            MessageContext.RestoreContext(null);
        }

        // Assert -- one RetryAttempted for the one retry, on the dispatch's own MessageId
        var observed = collector.Collected.Should().ContainSingle().Subject;
        observed.MessageId.Should().Be(context.MessageId);
        var retry = observed.Observation.Should().BeOfType<RetryAttempted>().Subject;
        retry.Attempt.Should().Be(1);
        retry.ErrorMessage.Should().Be("attempt 1 failed");
        retry.ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        retry.DelayMs.Should().Be(0);
    }

    private static ServiceProvider BuildProvider(
        Action<DispatchRetryOptions>? configure = null,
        RecordingLogger<DispatchRetryRunner>? logger = null,
        bool withPolicy = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<AttemptProbe>();
        services.AddScoped<ServiceProviderCapture>();
        if (logger is not null)
        {
            services.AddSingleton<ILogger<DispatchRetryRunner>>(logger);
        }

        if (withPolicy)
        {
            new ConduitBuilder(services).AddDispatchRetry<RecordingRetryPolicy>(options =>
            {
                // No wait unless a test asks for one.
                options.Delays.Clear();
                options.Delays.Add(new(TimeSpan.Zero, TimeSpan.Zero));
                configure?.Invoke(options);
            });
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static RecordingRetryPolicy GetPolicy(IServiceProvider provider)
        => (RecordingRetryPolicy)provider.GetRequiredService<IDispatchRetryPolicy>();
}
