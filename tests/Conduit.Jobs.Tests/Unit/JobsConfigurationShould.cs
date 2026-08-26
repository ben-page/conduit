using System.Globalization;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Jobs;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Runtime;
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

    [TestMethod]
    public void RejectACoalescingTriggerThatDeclaresNoMerge()
    {
        // Arrange — there is no safe default merge: last-write-wins narrows the scope, which is the
        // silent staleness the merge contract exists to forbid
        static void Configure(JobsBuilder jobs)
            => jobs.On<PunchChanged>("rebuild")
                .CoalesceBy(punch => punch.LocationId)
                .Run(punch => new RecomputeWorkstation(punch.LocationId, punch.From));

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*Merge*");
    }

    [TestMethod]
    public void RecordTheCoalesceKeyCommandTypeAndMergeOnTheTrigger()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new ConduitBuilder(services);

        // Act
        builder.AddJobs(jobs => jobs.On<PunchChanged>("rebuild")
            .CoalesceBy(punch => punch.LocationId)
            .Run(punch => new RecomputeWorkstation(punch.LocationId, punch.From))
            .Merge((pending, incoming) => pending.From <= incoming.From ? pending : incoming));

        // Assert
        var registry = services
            .Single(descriptor => descriptor.ServiceType == typeof(ScheduleRegistry))
            .ImplementationInstance
            .Should().BeOfType<ScheduleRegistry>().Subject;

        var trigger = registry.Triggers.Should().ContainSingle().Subject;
        trigger.Name.Should().Be("rebuild");
        trigger.CommandType.Should().Be<RecomputeWorkstation>();
        trigger.CoalesceKey.Should().NotBeNull();
        trigger.CoalesceKey!(new PunchChanged("loc-7", default)).Should().Be("loc-7");
        trigger.Merge.Should().NotBeNull();
    }

    [TestMethod]
    public void FormatACoalesceKeyWithTheInvariantCulture()
    {
        // Arrange — a key formatted per-host culture would stop two hosts agreeing on one scope
        var services = new ServiceCollection();
        var builder = new ConduitBuilder(services);
        builder.AddJobs(jobs => jobs.On<NumericPunch>("rebuild")
            .CoalesceBy(punch => punch.Sequence)
            .Run(punch => new RecordedJob(punch.Sequence.ToString(CultureInfo.InvariantCulture)))
            .Merge((pending, incoming) => incoming));

        var registry = services
            .Single(descriptor => descriptor.ServiceType == typeof(ScheduleRegistry))
            .ImplementationInstance as ScheduleRegistry;

        // Act
        var key = registry!.Triggers[0].CoalesceKey!(new NumericPunch(1234.5m));

        // Assert
        key.Should().Be("1234.5");
    }

    [TestMethod]
    public void RejectACoalesceKeyThatFormatsToNothing()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new ConduitBuilder(services);
        builder.AddJobs(jobs => jobs.On<PunchChanged>("rebuild")
            .CoalesceBy(punch => punch.LocationId)
            .Run(punch => new RecomputeWorkstation(punch.LocationId, punch.From))
            .Merge((pending, incoming) => incoming));

        var registry = services
            .Single(descriptor => descriptor.ServiceType == typeof(ScheduleRegistry))
            .ImplementationInstance as ScheduleRegistry;

        // Act — an empty key would collapse every scope for the trigger into one
        var act = () => registry!.Triggers[0].CoalesceKey!(new PunchChanged("   ", default));

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*coalesce key*");
    }

    private static void AddJobsForTest(Action<JobsBuilder> configure)
    {
        var builder = new ConduitBuilder(new ServiceCollection());
        builder.AddJobs(configure);
    }
}
