using System.Globalization;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Jobs;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Conduit.Persistence.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    public void RejectAClaimTimeoutAboveADay()
    {
        // Arrange
        static void Configure(JobsBuilder jobs) => jobs.ClaimTimeout = TimeSpan.FromDays(1) + TimeSpan.FromTicks(1);

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert — the heartbeat bounds each renewal by the time left to the claim timeout, which a
        // timer cannot hold past about 49 days, and a crashed host's run waits out the whole timeout
        act.Should().Throw<InvalidOperationException>().WithMessage("*ClaimTimeout*at most one day*");
    }

    [TestMethod]
    [DataRow(0, DisplayName = "Zero")]
    [DataRow(-1, DisplayName = "Negative")]
    public void RejectANonPositiveMaxRuntimeOnASchedule(int seconds)
    {
        // Arrange — a ceiling of no time would cancel every run at its first heartbeat
        void Configure(JobsBuilder jobs)
            => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).MaxRuntime(TimeSpan.FromSeconds(seconds))
                .Run(() => new NoopJob());

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*schedule 'warm'*MaxRuntime*positive*");
    }

    [TestMethod]
    public void RejectANonPositiveMaxRuntimeOnAnEventTrigger()
    {
        // Arrange
        static void Configure(JobsBuilder jobs)
            => jobs.On<ThingHappened>("notify").MaxRuntime(TimeSpan.Zero).Run(thing => new RecordedJob(thing.Label));

        // Act
        var act = () => AddJobsForTest(Configure, withEngine: true);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*trigger 'notify'*MaxRuntime*positive*");
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
    public void CarryRunAtStartOnAnIntervalSchedule()
    {
        // Arrange
        static void Configure(JobsBuilder jobs)
            => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).RunAtStart().Run(() => new NoopJob());

        // Act
        var registry = RegistryFor(Configure);

        // Assert
        registry.Schedules.Single().RunsAtStart.Should().BeTrue();
    }

    [TestMethod]
    public void NotRunAtStartUnlessAsked()
    {
        // Arrange
        static void Configure(JobsBuilder jobs)
            => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new NoopJob());

        // Act
        var registry = RegistryFor(Configure);

        // Assert
        registry.Schedules.Single().RunsAtStart.Should().BeFalse();
    }

    [TestMethod]
    public void RefuseRunAtStartOnACronSchedule()
    {
        // Arrange — a cron schedule's start-up catch-up is OnMisfire(RunImmediately); allowing both
        // would let one start fire the job twice
        static void Configure(JobsBuilder jobs)
            => jobs.Schedule("nightly").Cron("0 2 * * *").RunAtStart().Run(() => new NoopJob());

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'nightly'*RunAtStart()*OnMisfire(MisfirePolicy.RunImmediately)*");
    }

    [TestMethod]
    public void BuildAnOnDemandScheduleWithNoTimetable()
    {
        // Arrange
        static void Configure(JobsBuilder jobs)
            => jobs.Schedule("rederive")
                .OnDemand()
                .AllowOverlap()
                .MaxRuntime(TimeSpan.FromMinutes(10))
                .Run(() => new NoopJob());

        // Act
        var registry = RegistryFor(Configure);

        // Assert — overlap and the runtime ceiling carry as for any schedule; the timetable is empty
        var schedule = registry.Schedules.Should().ContainSingle().Subject;
        schedule.IsOnDemand.Should().BeTrue();
        schedule.AllowsOverlap.Should().BeTrue();
        schedule.MaxRuntime.Should().Be(TimeSpan.FromMinutes(10));
        schedule.RunsAtStart.Should().BeFalse();
        schedule.NextOccurrenceAfter(DateTimeOffset.MinValue).Should().BeNull();
    }

    [TestMethod]
    public void IgnoreAMisfirePolicyOnAnOnDemandSchedule_AsOnAnIntervalSchedule()
    {
        // Arrange — an on-demand schedule has nothing to miss
        static void Configure(JobsBuilder jobs)
        {
            jobs.Schedule("rederive").OnDemand().OnMisfire(MisfirePolicy.RunImmediately).Run(() => new NoopJob());
            jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).OnMisfire(MisfirePolicy.RunImmediately).Run(() => new NoopJob());
        }

        // Act
        var registry = RegistryFor(Configure);

        // Assert
        registry.Schedules.Select(schedule => schedule.Misfire).Should().AllBeEquivalentTo(MisfirePolicy.Skip);
    }

    [TestMethod]
    public void RejectAScheduleDeclaringBothCronAndOnDemand()
    {
        // Arrange
        static void Configure(JobsBuilder jobs)
            => jobs.Schedule("rederive").Cron("0 2 * * *").OnDemand().Run(() => new NoopJob());

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'rederive'*both Cron(...) and OnDemand()*Choose one*");
    }

    [TestMethod]
    public void RejectAScheduleDeclaringBothIntervalAndOnDemand()
    {
        // Arrange
        static void Configure(JobsBuilder jobs)
            => jobs.Schedule("rederive").OnDemand().Every(TimeSpan.FromMinutes(5)).Run(() => new NoopJob());

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'rederive'*both Every(...) and OnDemand()*Choose one*");
    }

    [TestMethod]
    public void RefuseRunAtStartOnAnOnDemandSchedule()
    {
        // Arrange — a run at every start would give the schedule a timetable after all
        static void Configure(JobsBuilder jobs)
            => jobs.Schedule("rederive").OnDemand().RunAtStart().Run(() => new NoopJob());

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'rederive'*RunAtStart()*OnDemand()*");
    }

    [TestMethod]
    public void RejectAScheduleDeclaringNoTimetableNamingEveryKind()
    {
        // Arrange
        static void Configure(JobsBuilder jobs)
            => jobs.Schedule("rollup").Run(() => new NoopJob());

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'rollup'*Cron(...)*Every(...)*OnDemand()*");
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
            => jobs.On<BadgeScanned>("rebuild")
                .CoalesceBy(scan => scan.LocationId)
                .Run(scan => new RebuildLocationSummary(scan.LocationId, scan.From));

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
        builder.AddJobs(jobs => jobs.On<BadgeScanned>("rebuild")
            .CoalesceBy(scan => scan.LocationId)
            .Run(scan => new RebuildLocationSummary(scan.LocationId, scan.From))
            .Merge((pending, incoming) => pending.From <= incoming.From ? pending : incoming));

        // Assert
        var registry = services
            .Single(descriptor => descriptor.ServiceType == typeof(ScheduleRegistry))
            .ImplementationInstance
            .Should().BeOfType<ScheduleRegistry>().Subject;

        var trigger = registry.Triggers.Should().ContainSingle().Subject;
        trigger.Name.Should().Be("rebuild");
        trigger.CommandType.Should().Be<RebuildLocationSummary>();
        trigger.CoalesceKey.Should().NotBeNull();
        trigger.CoalesceKey!(new BadgeScanned("loc-7", default)).Should().Be("loc-7");
        trigger.Merge.Should().NotBeNull();
    }

    [TestMethod]
    public void FormatACoalesceKeyWithTheInvariantCulture()
    {
        // Arrange — a key formatted per-host culture would stop two hosts agreeing on one scope
        var services = new ServiceCollection();
        var builder = new ConduitBuilder(services);
        builder.AddJobs(jobs => jobs.On<NumericScan>("rebuild")
            .CoalesceBy(scan => scan.Sequence)
            .Run(scan => new RecordedJob(scan.Sequence.ToString(CultureInfo.InvariantCulture)))
            .Merge((pending, incoming) => incoming));

        var registry = services
            .Single(descriptor => descriptor.ServiceType == typeof(ScheduleRegistry))
            .ImplementationInstance as ScheduleRegistry;

        // Act
        var key = registry!.Triggers[0].CoalesceKey!(new NumericScan(1234.5m));

        // Assert
        key.Should().Be("1234.5");
    }

    [TestMethod]
    public void RejectACoalesceKeyThatFormatsToNothing()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new ConduitBuilder(services);
        builder.AddJobs(jobs => jobs.On<BadgeScanned>("rebuild")
            .CoalesceBy(scan => scan.LocationId)
            .Run(scan => new RebuildLocationSummary(scan.LocationId, scan.From))
            .Merge((pending, incoming) => incoming));

        var registry = services
            .Single(descriptor => descriptor.ServiceType == typeof(ScheduleRegistry))
            .ImplementationInstance as ScheduleRegistry;

        // Act — an empty key would collapse every scope for the trigger into one
        var act = () => registry!.Triggers[0].CoalesceKey!(new BadgeScanned("   ", default));

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*coalesce key*");
    }

    [TestMethod]
    public void CarryEveryTuningKnobDeclaredOnTheBuilderIntoTheResolvedOptions()
    {
        // Arrange — every value differs from its default, so a knob the registration forgets to
        // carry shows up as the default rather than as what was declared
        var services = new ServiceCollection();
        var builder = new ConduitBuilder(services);

        // Act
        builder.AddJobs(jobs =>
        {
            jobs.HeartbeatInterval = TimeSpan.FromSeconds(15);
            jobs.ClaimTimeout = TimeSpan.FromMinutes(1);
            jobs.MaxAttempts = 5;
            jobs.RetryBackoff = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(90)];
            jobs.FailedRetention = TimeSpan.FromDays(30);
            jobs.CompletedRetention = TimeSpan.FromHours(6);
            jobs.CleanupInterval = TimeSpan.FromMinutes(10);
            jobs.AutoCreateTable = false;
            jobs.HistoryRetention = TimeSpan.FromDays(90);
            jobs.ManageOnly = true;
        });

        // Assert
        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<JobsOptions>>().Value;

        options.HeartbeatInterval.Should().Be(TimeSpan.FromSeconds(15));
        options.ClaimTimeout.Should().Be(TimeSpan.FromMinutes(1));
        options.MaxAttempts.Should().Be(5);
        options.RetryBackoff.Should().Equal(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(90));
        options.FailedRetention.Should().Be(TimeSpan.FromDays(30));
        options.CompletedRetention.Should().Be(TimeSpan.FromHours(6));
        options.CleanupInterval.Should().Be(TimeSpan.FromMinutes(10));
        options.AutoCreateTable.Should().BeFalse();
        options.HistoryRetention.Should().Be(TimeSpan.FromDays(90));
        options.ManageOnly.Should().BeTrue();
    }

    [TestMethod]
    public void RejectAnEventTriggerOnAManageOnlyHost()
    {
        // Arrange — a trigger fires only through the listener the runner registers, so on a host
        // that registers no runner the declaration would silently never fire
        static void Configure(JobsBuilder jobs)
        {
            jobs.ManageOnly = true;
            jobs.On<ThingHappened>("notify").Run(thing => new RecordedJob(thing.Label));
        }

        // Act
        var act = () => AddJobsForTest(Configure, withEngine: true);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*'notify'*ManageOnly*never fire*");
    }

    [TestMethod]
    public void RejectAnEventTriggerOnAManageOnlyHostWithoutAnEngine()
    {
        // Arrange
        static void Configure(JobsBuilder jobs)
        {
            jobs.ManageOnly = true;
            jobs.On<ThingHappened>("notify").Run(thing => new RecordedJob(thing.Label));
        }

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*'notify'*ManageOnly*");
    }

    [TestMethod]
    public void RejectAScheduleOnAManageOnlyHostWithoutAnEngine()
    {
        // Arrange — no shared ledger for another host to run it from, and this host runs nothing
        static void Configure(JobsBuilder jobs)
        {
            jobs.ManageOnly = true;
            jobs.Schedule("rollup").Every(TimeSpan.FromMinutes(1)).Run(() => new NoopJob());
        }

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*'rollup'*no persistence engine*never run*");
    }

    [TestMethod]
    public void AcceptAScheduleOnAManageOnlyHostWithAnEngine()
    {
        // Arrange — the host lists and triggers another host's schedule, the ProducerOnly use case
        static void Configure(JobsBuilder jobs)
        {
            jobs.ManageOnly = true;
            jobs.Schedule("rollup").Every(TimeSpan.FromMinutes(1)).Run(() => new NoopJob());
        }

        // Act
        var act = () => AddJobsForTest(Configure, withEngine: true);

        // Assert
        act.Should().NotThrow();
    }

    [TestMethod]
    public void AcceptAManageOnlyHostWithoutAnEngineThatDeclaresNothing()
    {
        // Arrange
        static void Configure(JobsBuilder jobs) => jobs.ManageOnly = true;

        // Act
        var act = () => AddJobsForTest(Configure);

        // Assert
        act.Should().NotThrow();
    }

    private static void AddJobsForTest(Action<JobsBuilder> configure, bool withEngine = false)
    {
        var builder = new ConduitBuilder(new ServiceCollection());
        if (withEngine)
        {
            builder.UseSqlite("Data Source=:memory:");
        }

        builder.AddJobs(configure);
    }

    private static ScheduleRegistry RegistryFor(Action<JobsBuilder> configure)
    {
        var services = new ServiceCollection();
        new ConduitBuilder(services).AddJobs(configure);

        return services
            .Single(descriptor => descriptor.ServiceType == typeof(ScheduleRegistry))
            .ImplementationInstance
            .Should().BeOfType<ScheduleRegistry>().Subject;
    }
}
