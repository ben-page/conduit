using System.Text.Json;
using AwesomeAssertions;
using Conduit.Jobs;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

[TestClass]
public class CoalescingTriggerShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 26, 1, 59, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task MergeABurstIntoOneScopeThatWidensTheWindow()
    {
        // Arrange — a delayed trigger, so nothing picks the scope up while the burst arrives
        var spy = new WorkstationSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartDelayedHostAsync(database, new FakeTimeProvider(Start), spy);
        var mediator = host.GetRequiredService<IMediator>();

        // Act — three punches at one workstation, arriving newest window first
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(9)));
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(8)));
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(10)));

        // Assert — one scope, and the merge kept the EARLIEST window: widen, never narrow
        var scope = await JobsDatabaseProbe.WaitForOnlyScopeAsync(database);
        scope.ScopeKey.Should().Be("rebuild@loc-1");
        scope.Version.Should().Be(2, "three events are one insert and two merges");
        scope.DueAt.Should().Be(Start.AddMinutes(30), "merging is leading-edge and never moves the due time");

        var merged = JsonSerializer.Deserialize<RecomputeWorkstation>(
            scope.Payload, ConduitRuntime.JsonOptions);
        merged!.From.Should().Be(Start.AddHours(8), "the merge keeps the earliest window: widen, never narrow");

        var runs = await JobsDatabaseProbe.CountRunsAsync(database);
        runs.Should().Be(0, "the scope is not due for another half hour");
    }

    [TestMethod]
    public async Task KeepScopesForDifferentKeysApart()
    {
        // Arrange
        var spy = new WorkstationSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartDelayedHostAsync(database, new FakeTimeProvider(Start), spy);
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(9)));
        await mediator.PublishAsync(new PunchChanged("loc-2", Start.AddHours(9)));

        // Assert — coalescing merges work that IS the same, and must not merge work that is not
        var store = JobsDatabaseProbe.StoreFor(database);
        var scopes = await store.GetScopesAsync(null, CancellationToken.None);
        scopes.Select(scope => scope.ScopeKey)
            .Should().BeEquivalentTo(["rebuild@loc-1", "rebuild@loc-2"]);
    }

    [TestMethod]
    public async Task SurviveTwoHostsMergingIntoOneScopeConcurrently()
    {
        // Arrange — two replicas of one application against one database. A local publish reaches only
        // the publishing host's listeners, so each host merges its own event into the shared scope.
        var timeProvider = new FakeTimeProvider(Start);
        await using var database = SharedSqliteDatabase.Create();
        await using var first = await StartDelayedHostAsync(database, timeProvider, new WorkstationSpy());
        await using var second = await StartDelayedHostAsync(database, timeProvider, new WorkstationSpy());

        // Act — both hosts race on one coalesce key
        await Task.WhenAll(
            first.GetRequiredService<IMediator>().PublishAsync(new PunchChanged("loc-1", Start.AddHours(9))),
            second.GetRequiredService<IMediator>().PublishAsync(new PunchChanged("loc-1", Start.AddHours(8))));

        // Assert — a lost update here would NARROW the window, which loses work rather than merely
        // duplicating it. The survivor must be the merge of both, never either input alone.
        var scope = await JobsDatabaseProbe.WaitForOnlyScopeAsync(database);
        scope.Version.Should().Be(1, "one host inserted and the other merged");

        JsonSerializer.Deserialize<RecomputeWorkstation>(scope.Payload, ConduitRuntime.JsonOptions)!
            .From.Should().Be(Start.AddHours(8), "whichever host won the insert, the earlier window survives");
    }

    [TestMethod]
    public async Task RunAnImmediateCoalescingTriggerOnTheReceivingHost()
    {
        // Arrange
        var spy = new WorkstationSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartImmediateHostAsync(database, new FakeTimeProvider(Start), spy);

        // Act
        await host.GetRequiredService<IMediator>()
            .PublishAsync(new PunchChanged("loc-1", Start.AddHours(9)));

        // Assert — latency matters: the host that received the event runs it, without waiting a sweep
        await spy.WaitForCountAsync(1);
        spy.Rebuilds.Should().ContainSingle().Which.LocationId.Should().Be("loc-1");
    }

    [TestMethod]
    public async Task ParkASecondScopeForEventsArrivingDuringARun()
    {
        // Arrange — the first rebuild blocks, so the burst behind it has somewhere to accumulate
        var spy = new WorkstationSpy { BlockUntilReleased = true };
        var timeProvider = new FakeTimeProvider(Start);
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartImmediateHostAsync(database, timeProvider, spy);
        var mediator = host.GetRequiredService<IMediator>();

        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(9)));
        await spy.WaitForCountAsync(1);

        // Act — three more punches while the first rebuild is in flight
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(8)));
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(7)));
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(11)));

        // Assert — a claimed run's scope is fixed, so the three form ONE new scope, not three runs
        var scope = await JobsDatabaseProbe.WaitForOnlyScopeAsync(database);
        scope.Version.Should().Be(2);
        JsonSerializer.Deserialize<RecomputeWorkstation>(scope.Payload, ConduitRuntime.JsonOptions)!
            .From.Should().Be(Start.AddHours(7));

        spy.Count.Should().Be(1, "the second run starts only once the first releases");
        (await JobsDatabaseProbe.CountRunsAsync(database)).Should().Be(
            1, "the two-row promise is about the ledger, not the handler");

        // The waiting scope becomes the second run once the sweep drains scopes — see Task 6's
        // RunTheWaitingScopeOnceTheRunAheadOfItClears.
        spy.BlockUntilReleased = false;
        spy.ReleaseOne();
    }

    [TestMethod]
    public async Task CollapseASecondPickupOfOneScope()
    {
        // Arrange — a host dying between materializing a run and deleting its scope leaves the scope
        // alive, so the next sweep picks it up again. The window pinned here is the one where the
        // first run is still OUTSTANDING: its row holds the key, the second materialize is refused,
        // and nothing double-runs. The property does not reach past that — once the run completes,
        // CompleteAsync deletes its row, the key is free, and an orphaned scope re-materializes and
        // runs the same merged command a second time. At-least-once is the contract, and this design
        // resolves every such window toward a duplicate rather than a drop.
        await using var database = SharedSqliteDatabase.Create();
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);

        static JobRun Run() => new()
        {
            JobKey = "rebuild@loc-1",
            ScheduleName = "rebuild",
            MessageType = "Conduit.Jobs.Tests.Support.RecomputeWorkstation",
            Payload = """{"locationId":"loc-1"}""",
            RunAt = Start,
            Status = JobRunStatus.Pending,
        };

        // Act — the run key is the scope key alone, so both attempts compute the same key
        var first = await store.TryMaterializeAsync(Run(), CancellationToken.None);
        var second = await store.TryMaterializeAsync(Run(), CancellationToken.None);

        // Assert
        first.Should().BeTrue();
        second.Should().BeFalse();
        (await JobsDatabaseProbe.CountRunsAsync(database)).Should().Be(1);
    }

    [TestMethod]
    public async Task RunCoalescedWorkAfterATerminalRunHasHeldItsKey()
    {
        // Arrange — a run that exhausts its attempts (or is cancelled) goes terminal but stays in the
        // ledger until the retention purge, days later by default. The run key IS the coalesce key, so
        // that row must not be able to wedge every later unit of work behind it. Driven through the
        // store rather than a genuinely failing handler, so this stays fast and deterministic.
        await using var database = SharedSqliteDatabase.Create();
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);

        var stuck = await store.InsertClaimedAsync(
            new JobRun
            {
                JobKey = "rebuild@loc-1",
                ScheduleName = "rebuild",
                MessageType = "Conduit.Jobs.Tests.Support.RecomputeWorkstation",
                Payload = """{"locationId":"loc-1","from":"2026-08-25T10:59:00+00:00"}""",
                RunAt = Start,
            },
            "worker",
            Start,
            CancellationToken.None);
        await store.MarkTerminalAsync(
            stuck!, "worker", JobRunStatus.Failed, "boom", scheduleName: null, occurrence: null, Start,
            CancellationToken.None);

        // Blocked, so the assertion below observes the new run in flight rather than racing its own
        // completion — CompleteAsync deletes the row on success, which would make "gone" ambiguous.
        var spy = new WorkstationSpy { BlockUntilReleased = true };
        await using var host = await StartImmediateHostAsync(database, new FakeTimeProvider(Start), spy);

        // Act — a different unit of work arrives for the same coalesce key
        await host.GetRequiredService<IMediator>()
            .PublishAsync(new PunchChanged("loc-1", Start.AddHours(9)));

        // Assert — the exhausted occurrence must not stall a scope that is different work
        await spy.WaitForCountAsync(1);
        spy.Rebuilds.Should().ContainSingle().Which.LocationId.Should().Be("loc-1");

        // JobKey is unique, so one row existing at Claiming means the Failed row is gone, superseded
        // by this new, already-running run rather than merely left in place beside it.
        var run = await store.GetRunAsync("rebuild@loc-1", CancellationToken.None);
        run.Should().NotBeNull("the terminal row was superseded by the new, already-running run");
        run!.Status.Should().Be(JobRunStatus.Claiming, "this is the new run, not the failed one left in place");

        // The superseded row is gone from the ledger, but its failure is not gone from history — this
        // is the fix for the data-loss bug TryClearTerminalRunAsync's delete used to cause: the
        // Warning log was the only surviving record that the occurrence ever failed at all.
        var history = await store.QueryHistoryAsync(
            new JobHistoryFilter { JobKey = "rebuild@loc-1" }, CancellationToken.None);
        history.Entries.Should().ContainSingle().Which.Outcome.Should().Be(JobRunOutcome.Failed);

        spy.ReleaseOne();
    }

    [TestMethod]
    public async Task RunADelayedScopeWhenItComesDue()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new WorkstationSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartDelayedHostAsync(database, timeProvider, spy);
        var mediator = host.GetRequiredService<IMediator>();

        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(9)));
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(8)));
        await JobsDatabaseProbe.WaitForOnlyScopeAsync(database);

        // Act — the sweep, not the publishing host, is what eventually runs it
        await FakeClock.AdvanceUntilAsync(
            timeProvider, TimeSpan.FromMinutes(5), () => Task.FromResult(spy.Count > 0));

        // Assert — one run for two events, carrying the widened window
        spy.Count.Should().Be(1);
        spy.Rebuilds[0].From.Should().Be(Start.AddHours(8));
    }

    [TestMethod]
    public async Task NotMoveTheDueTimeWhenALaterEventMergesIn()
    {
        // Arrange — leading edge. Trailing edge would let a steady stream starve the run forever,
        // and the merge already widens the scope so the run still covers everything.
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new WorkstationSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartDelayedHostAsync(database, timeProvider, spy);
        var mediator = host.GetRequiredService<IMediator>();

        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(9)));
        var opened = await JobsDatabaseProbe.WaitForOnlyScopeAsync(database);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(8)));

        // Assert
        var merged = await JobsDatabaseProbe.WaitForOnlyScopeAsync(database);
        merged.Version.Should().Be(1, "the merge landed");
        merged.DueAt.Should().Be(opened.DueAt, "merging is leading-edge and must not reset the timer");
    }

    [TestMethod]
    public async Task RunTheWaitingScopeOnceTheRunAheadOfItClears()
    {
        // Arrange — a burst arriving during a rebuild parks in one scope. It cannot become a run
        // while the first still holds the coalesce key, so the sweep is what eventually starts it:
        // one in flight and one accumulating, never one run per event.
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new WorkstationSpy { BlockUntilReleased = true };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartImmediateHostAsync(database, timeProvider, spy);
        var mediator = host.GetRequiredService<IMediator>();

        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(9)));
        await spy.WaitForCountAsync(1);

        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(8)));
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(7)));
        await JobsDatabaseProbe.WaitForOnlyScopeAsync(database);

        // Act
        spy.BlockUntilReleased = false;
        spy.ReleaseOne();

        // Assert — three events, two runs, and the second carries the widened window
        await FakeClock.AdvanceUntilAsync(
            timeProvider, TimeSpan.FromMinutes(1), () => Task.FromResult(spy.Count >= 2));

        spy.Count.Should().Be(2);
        spy.Rebuilds[1].From.Should().Be(Start.AddHours(7));
    }

    [TestMethod]
    public async Task FailTheEventWhenMergeContentionIsNeverResolved()
    {
        // Arrange — a hot key that several replicas keep colliding on. Every compare-and-set is
        // refused, so the bounded retry runs out. Returning quietly there would let the listener
        // complete and the broker ack the event with its work in no scope at all — a drop, the one
        // outcome this design never resolves toward.
        await using var database = SharedSqliteDatabase.Create();
        var real = JobsDatabaseProbe.StoreFor(database);
        await real.EnsureTablesExistAsync(CancellationToken.None);

        // A scope has to exist already, or the first attempt opens one and never reaches the merge.
        // Due an hour out, so the running host's sweep leaves it alone.
        await real.TryInsertScopeAsync(
            new JobScope
            {
                ScopeKey = "rebuild@loc-1",
                ScopeId = Guid.NewGuid().ToString("N"),
                TriggerName = "rebuild",
                MessageType = "Conduit.Jobs.Tests.Support.RecomputeWorkstation",
                Payload = """{"locationId":"loc-1","from":"2026-08-26T10:59:00+00:00"}""",
                DueAt = Start.AddHours(1),
                Version = 0,
            },
            CancellationToken.None);

        await using var host = await StartDelayedHostAsync(
            database, new FakeTimeProvider(Start), new WorkstationSpy());

        var coalescer = new ScopeCoalescer(
            new ContendedJobStore(real),
            host.GetRequiredService<ClaimedJobExecutor>(),
            host.GetRequiredService<JobSweepSignal>(),
            NullLogger<ScopeCoalescer>.Instance);

        // Act
        var merge = async () => await coalescer.MergeAsync(
            CoalescingTrigger(),
            new PunchChanged("loc-1", Start.AddHours(8)),
            Start,
            CancellationToken.None);

        // Assert — the failure has to reach the transport, which can then retry or dead-letter it
        var thrown = await merge.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("rebuild@loc-1", "the operator needs the key named");
        thrown.Which.Message.Should().Contain("not merged");
    }

    /// <summary>
    /// The same trigger <see cref="StartDelayedHostAsync"/> declares, built directly so a test can
    /// drive <see cref="ScopeCoalescer"/> without going through a publish.
    /// </summary>
    private static JobTrigger CoalescingTrigger()
        => new(
            "rebuild",
            typeof(PunchChanged),
            delay: TimeSpan.FromMinutes(30),
            maxRuntime: null,
            createCommand: instance => ToCommand((PunchChanged)instance),
            coalesceKey: instance => ((PunchChanged)instance).LocationId,
            commandType: typeof(RecomputeWorkstation))
        {
            Merge = (pending, incoming) =>
                ((RecomputeWorkstation)pending).From <= ((RecomputeWorkstation)incoming).From
                    ? pending
                    : incoming,
        };

    private static RecomputeWorkstation ToCommand(PunchChanged punch)
        => new(punch.LocationId, punch.From);

    private static Task<TestHost> StartDelayedHostAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, WorkstationSpy spy)
        => JobsTestHost.StartAsync(
            jobs => jobs.On<PunchChanged>("rebuild")
                .After(TimeSpan.FromMinutes(30))
                .CoalesceBy(punch => punch.LocationId)
                .Run(punch => new RecomputeWorkstation(punch.LocationId, punch.From))
                .Merge((pending, incoming) => pending.From <= incoming.From ? pending : incoming),
            timeProvider: timeProvider,
            connectionString: database.ConnectionString,
            workstationSpy: spy);

    private static Task<TestHost> StartImmediateHostAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, WorkstationSpy spy)
        => JobsTestHost.StartAsync(
            jobs => jobs.On<PunchChanged>("rebuild")
                .CoalesceBy(punch => punch.LocationId)
                .Run(punch => new RecomputeWorkstation(punch.LocationId, punch.From))
                .Merge((pending, incoming) => pending.From <= incoming.From ? pending : incoming),
            timeProvider: timeProvider,
            connectionString: database.ConnectionString,
            workstationSpy: spy);
}
