using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Jobs;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Jobs.Tests.Unit;

[TestClass]
public class JobsConfigurationShould
{
    [TestMethod]
    public void RejectAClaimTimeoutThatCanExpireWithinTwoHeartbeats()
    {
        // Arrange
        static void Configure(JobsBuilder jobs)
        {
            jobs.HeartbeatInterval = TimeSpan.FromSeconds(30);
            jobs.ClaimTimeout = TimeSpan.FromSeconds(45);
        }

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert — this misconfiguration causes double execution, so it must fail loudly at startup
        act.Should().Throw<InvalidOperationException>().WithMessage("*executing it twice*");
    }

    [TestMethod]
    public void RejectDuplicateScheduleNames()
    {
        // Arrange
        static void Configure(JobsBuilder jobs)
        {
            jobs.Schedule("rollup").Every(TimeSpan.FromMinutes(1)).Run(() => new NoopJob());
            jobs.Schedule("rollup").Every(TimeSpan.FromMinutes(2)).Run(() => new NoopJob());
        }

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*must be unique*");
    }

    [TestMethod]
    public void RejectAScheduleDeclaringBothCronAndInterval()
    {
        // Arrange
        static void Configure(JobsBuilder jobs)
            => jobs.Schedule("rollup").Cron("0 2 * * *").Every(TimeSpan.FromMinutes(1)).Run(() => new NoopJob());

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Choose one*");
    }

    [TestMethod]
    public void RejectDuplicateTriggerNames()
    {
        // Arrange — two triggers on one event type both default to the event's type name, so they
        // would write the identical run key and insert-or-ignore would silently drop the second
        static void Configure(JobsBuilder jobs)
        {
            jobs.On<ThingHappened>().Run(thing => new RecordedJob(thing.Label));
            jobs.On<ThingHappened>().Run(thing => new RecordedJob(thing.Label));
        }

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*must be unique*");
    }

    [TestMethod]
    public void AllowTwoTriggersOnOneEventTypeWhenTheyAreNamed()
    {
        // Arrange
        static void Configure(JobsBuilder jobs)
        {
            jobs.On<ThingHappened>("rebuild").Run(thing => new RecordedJob(thing.Label));
            jobs.On<ThingHappened>("notify").Run(thing => new RecordedJob(thing.Label));
        }

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().NotThrow();
    }

    private static void AddJobsForTest(Action<JobsBuilder> configure)
    {
        var builder = new ConduitBuilder(new ServiceCollection());
        builder.AddJobs(configure);
    }
}
