using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

/// <summary>A job's command is dispatched through the mediator, so a retryable failure is retried inside one job run.</summary>
[TestClass]
public class JobRetryShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 59, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Occurrence = new(2026, 8, 25, 2, 0, 0, TimeSpan.Zero);
    private static readonly string JobKey = DurableJobScheduler.BuildJobKey("nightly", Occurrence);

    [TestMethod]
    public async Task Complete_in_one_run_a_job_whose_first_attempt_fails_retryably()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("nightly").Cron("0 2 * * *").Run(() => new FlakyCommand(Failures: 1)),
            timeProvider: timeProvider,
            connectionString: database.ConnectionString,
            configureServices: services =>
            {
                services.AddSingleton<RetryProbe>();
                services.AddScoped<ScopeProbe>();
            },
            configureConduit: conduit => conduit.AddDispatchRetry<TestRetryPolicy>(options =>
            {
                options.Delays.Clear();
                options.Delays.Add(new DispatchRetryDelay(TimeSpan.Zero, TimeSpan.Zero));
                options.Delays.Add(new DispatchRetryDelay(TimeSpan.Zero, TimeSpan.Zero));
            }));
        var probe = host.GetRequiredService<RetryProbe>();

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await JobsDatabaseProbe.WaitForDeletionAsync(database, JobKey);

        // Assert -- both attempts ran inside the one run; the run completed (its row is gone), it did not fail and wait
        probe.Sightings.Select(sighting => sighting.AttemptNumber).Should().Equal(1, 2);
        probe.Sightings.Select(sighting => sighting.ScopeId).Distinct().Should().HaveCount(2);
        var schedule = await JobsDatabaseProbe.StoreFor(database).GetScheduleAsync("nightly", CancellationToken.None);
        schedule!.LastCompletedOccurrence.Should().Be(Occurrence);
        (await JobsDatabaseProbe.CountRunsAsync(database)).Should().Be(0, "a failed run would have been left behind to retry");
    }
}
