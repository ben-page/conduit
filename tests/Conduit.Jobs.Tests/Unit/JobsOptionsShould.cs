using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Jobs.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Jobs.Tests.Unit;

[TestClass]
public class JobsOptionsShould
{
    [TestMethod]
    [DataRow(nameof(JobsOptions.HeartbeatInterval), 0)]
    [DataRow(nameof(JobsOptions.HeartbeatInterval), -1)]
    [DataRow(nameof(JobsOptions.MaxAttempts), 0)]
    [DataRow(nameof(JobsOptions.MaxAttempts), -1)]
    [DataRow(nameof(JobsOptions.CleanupInterval), 0)]
    [DataRow(nameof(JobsOptions.CleanupInterval), -1)]
    [DataRow(nameof(JobsOptions.RetryBackoff), 0)]
    [DataRow(nameof(JobsOptions.RetryBackoff), -1)]
    public void RejectNonPositiveValues(string option, int value)
    {
        // Arrange — a zero heartbeat renews in a busy loop and a zero cleanup purges in one, a run with no
        // attempts never runs, and a zero backoff retries a failing run on every sweep

        // Act
        var act = () => new ConduitBuilder(new ServiceCollection()).AddJobs(jobs => Set(jobs, option, value));

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage($"*JobsOptions.{option}*positive*");
    }

    [TestMethod]
    public void RejectAnEmptyRetryBackoff()
    {
        // Act
        var act = () => new ConduitBuilder(new ServiceCollection()).AddJobs(jobs => jobs.RetryBackoff = []);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*JobsOptions.RetryBackoff*empty*");
    }

    [TestMethod]
    public void RepeatTheLastBackoffForLaterAttempts()
    {
        // Arrange
        var options = new JobsOptions { RetryBackoff = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)] };

        // Act
        var waits = Enumerable.Range(1, 4).Select(options.BackoffAfter).ToList();

        // Assert
        waits.Should().Equal(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    [TestMethod]
    public void DefaultToOneTwoAndFourMinutesOfBackoffAndAnHourlyCleanup()
    {
        // Arrange
        var options = new JobsOptions();

        // Assert — the backoff the runtime had hard-coded before it was a knob
        options.RetryBackoff.Should().Equal(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4));
        options.CleanupInterval.Should().Be(TimeSpan.FromHours(1));
    }

    [TestMethod]
    [DataRow(nameof(JobsOptions.FailedRetention))]
    [DataRow(nameof(JobsOptions.CompletedRetention))]
    [DataRow(nameof(JobsOptions.HistoryRetention))]
    public void RejectANegativeRetention(string option)
    {
        // Arrange — the cleanup subtracts a retention from now, so a negative one purges rows newer than now

        // Act
        var act = () => new ConduitBuilder(new ServiceCollection()).AddJobs(jobs => Set(jobs, option, -1));

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage($"*JobsOptions.{option}*negative*");
    }

    [TestMethod]
    public void AcceptAZeroRetention()
    {
        // Arrange — zero purges a terminal row at the next cleanup, which is a choice rather than a mistake
        static void Configure(JobsBuilder jobs)
        {
            jobs.FailedRetention = TimeSpan.Zero;
            jobs.CompletedRetention = TimeSpan.Zero;
            jobs.HistoryRetention = TimeSpan.Zero;
        }

        // Act
        var act = () => new ConduitBuilder(new ServiceCollection()).AddJobs(Configure);

        // Assert
        act.Should().NotThrow();
    }

    private static void Set(JobsBuilder jobs, string option, int value)
    {
        var duration = TimeSpan.FromSeconds(value);
        switch (option)
        {
            case nameof(JobsOptions.HeartbeatInterval):
                jobs.HeartbeatInterval = duration;
                break;
            case nameof(JobsOptions.MaxAttempts):
                jobs.MaxAttempts = value;
                break;
            case nameof(JobsOptions.CleanupInterval):
                jobs.CleanupInterval = duration;
                break;
            case nameof(JobsOptions.RetryBackoff):
                jobs.RetryBackoff = [TimeSpan.FromMinutes(1), duration];
                break;
            case nameof(JobsOptions.FailedRetention):
                jobs.FailedRetention = duration;
                break;
            case nameof(JobsOptions.CompletedRetention):
                jobs.CompletedRetention = duration;
                break;
            case nameof(JobsOptions.HistoryRetention):
                jobs.HistoryRetention = duration;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(option), option, "Not a jobs option this test sets.");
        }
    }
}
