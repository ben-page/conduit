using AwesomeAssertions;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Tests.Support;

namespace Conduit.Jobs.Tests.Integration;

[TestClass]
public class JobStoreShould
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Start = new(2026, 8, 26, 1, 59, 0, TimeSpan.Zero);
    private const string Key = "nightly@2026-08-25T02:00:00Z";

    [TestMethod]
    public async Task ClaimADueRunExactlyOnceAcrossConcurrentWorkers()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);

        // Act — two workers race for the same due row
        var first = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);
        var second = await fixture.Store.ClaimDueAsync("worker-b", Now, 10, CancellationToken.None);

        // Assert
        first.Should().HaveCount(1);
        second.Should().BeEmpty("a claimed row must not be claimable by a second worker");
    }

    [TestMethod]
    public async Task NotMaterializeTheSameOccurrenceTwice()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        var run = NewRun(Key, Now);

        // Act — three replicas racing to materialize one occurrence
        var first = await fixture.Store.TryMaterializeAsync(run, CancellationToken.None);
        var second = await fixture.Store.TryMaterializeAsync(run, CancellationToken.None);
        var third = await fixture.Store.TryMaterializeAsync(run, CancellationToken.None);

        // Assert
        new[] { first, second, third }.Count(inserted => inserted).Should().Be(1);
    }

    [TestMethod]
    public async Task NotClaimARunThatIsNotDueYet()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now.AddMinutes(30)), CancellationToken.None);

        // Act
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);

        // Assert — RunAt is the claim's eligibility column, so a delayed run stays untouched
        claimed.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ReportCancellationThroughTheHeartbeat()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);

        // Act
        await fixture.Store.RequestCancellationAsync(Key, CancellationToken.None);
        var beat = await fixture.Store.HeartbeatAsync(
            claimed[0].Id, "worker-a", Now.AddSeconds(30), CancellationToken.None);

        // Assert — the running host learns of cancellation without a separate query
        beat.ClaimHeld.Should().BeTrue();
        beat.CancellationRequested.Should().BeTrue();
    }

    [TestMethod]
    public async Task ReportTheClaimLostWhenAnotherWorkerTookIt()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);

        // Act — a stale claim is taken by another worker, then the original heartbeats
        var stolen = await fixture.Store.ClaimDueAsync("worker-b", Now.AddHours(1), 10, CancellationToken.None);
        var beat = await fixture.Store.HeartbeatAsync(
            claimed[0].Id, "worker-a", Now.AddHours(1), CancellationToken.None);

        // Assert — the original must stop, not keep running work it no longer owns
        stolen.Should().HaveCount(1);
        beat.ClaimHeld.Should().BeFalse();
    }

    [TestMethod]
    public async Task NotHandAStaleClaimToAnotherWorkerWhileItIsBeingHeartbeated()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);

        // Act — the holder renews past the 2-minute claim timeout, as a long-running job does
        var later = Now.AddMinutes(3);
        await fixture.Store.HeartbeatAsync(claimed[0].Id, "worker-a", later, CancellationToken.None);
        var stolen = await fixture.Store.ClaimDueAsync(
            "worker-b", later.AddSeconds(1), 10, CancellationToken.None);

        // Assert — without renewal, 'long job' and 'dead host' would be indistinguishable
        stolen.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DeleteTheRunAndAdvanceTheCursorTogetherOnCompletion()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.ReconcileSchedulesAsync(["nightly"], CancellationToken.None);
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);

        // Act
        await fixture.Store.CompleteAsync(claimed[0].Id, "nightly", Now, CancellationToken.None);

        // Assert — once the row is gone the cursor is the only thing preventing a re-run
        (await fixture.Store.GetRunAsync(Key, CancellationToken.None)).Should().BeNull();
        var schedule = await fixture.Store.GetScheduleAsync("nightly", CancellationToken.None);
        schedule!.LastCompletedOccurrence.Should().Be(Now);
    }

    [TestMethod]
    public async Task TreatARetryPendingRunAsOutstanding()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);

        // Act
        var attempts = await fixture.Store.IncrementAttemptAsync(
            claimed[0].Id, Now.AddMinutes(1), "boom", CancellationToken.None);

        // Assert — the previous occurrence has not succeeded, so a second must not start
        attempts.Should().Be(1);
        (await fixture.Store.HasOutstandingAsync("nightly", CancellationToken.None)).Should().BeTrue();
    }

    [TestMethod]
    public async Task NotTreatATerminalRunAsOutstanding()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);

        // Act
        await fixture.Store.MarkTerminalAsync(
            claimed[0].Id, JobRunStatus.Failed, "gave up", scheduleName: null, occurrence: null,
            CancellationToken.None);

        // Assert — a permanently failing occurrence stalls its schedule only until attempts run out
        (await fixture.Store.HasOutstandingAsync("nightly", CancellationToken.None)).Should().BeFalse();
    }

    [TestMethod]
    public async Task RefuseToFlagATerminalRunForCancellation()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);
        await fixture.Store.MarkTerminalAsync(
            claimed[0].Id, JobRunStatus.Failed, null, scheduleName: null, occurrence: null,
            CancellationToken.None);

        // Act
        var flagged = await fixture.Store.RequestCancellationAsync(Key, CancellationToken.None);

        // Assert — the caller must be able to tell 'already finished' from 'will stop'
        flagged.Should().BeFalse();
    }

    [TestMethod]
    public async Task InsertAnImmediateTriggerAlreadyClaimedAndIgnoreARedelivery()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        var run = new JobRun
        {
            JobKey = "OrderPlaced@msg-1",
            ScheduleName = "OrderPlaced",
            MessageType = "Tests.SendReceipt",
            Payload = "{}",
            RunAt = Now,
        };

        // Act — the same event delivered twice, as a broker redelivery produces
        var first = await fixture.Store.InsertClaimedAsync(run, "host-a", Now, CancellationToken.None);
        var second = await fixture.Store.InsertClaimedAsync(run, "host-a", Now, CancellationToken.None);

        // Assert — the deterministic key makes the trigger idempotent for free
        first.Should().NotBeNull();
        first!.Status.Should().Be(JobRunStatus.Claiming);
        first.ClaimToken.Should().Be("host-a");
        second.Should().BeNull("a redelivered event must not produce a second run");
    }

    [TestMethod]
    public async Task ReleaseAClaimWithoutTouchingTheAttemptCount()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);

        // Act
        await fixture.Store.ReleaseAsync(claimed[0].Id, claimed[0].ClaimToken!, CancellationToken.None);

        // Assert — a shutdown handover is not a failed attempt, and another host takes it immediately
        var released = await fixture.Store.GetRunAsync(Key, CancellationToken.None);
        released!.Status.Should().Be(JobRunStatus.Pending);
        released.AttemptCount.Should().Be(0);
        released.ClaimToken.Should().BeNull();
        var reclaimed = await fixture.Store.ClaimDueAsync("worker-b", Now, 10, CancellationToken.None);
        reclaimed.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task NotReleaseAClaimThatAnotherWorkerNowHolds()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var original = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);
        var stolen = await fixture.Store.ClaimDueAsync("worker-b", Now.AddHours(1), 10, CancellationToken.None);

        // Act — the original host drains and tries to hand its claim back
        await fixture.Store.ReleaseAsync(
            original[0].Id, original[0].ClaimToken!, CancellationToken.None);

        // Assert — releasing unconditionally would tear the row out from under the new holder
        var run = await fixture.Store.GetRunAsync(Key, CancellationToken.None);
        run!.Status.Should().Be(JobRunStatus.Claiming);
        run.ClaimToken.Should().Be(stolen[0].ClaimToken);
    }

    [TestMethod]
    public async Task MarkTerminalAndAdvanceTheCursorTogether()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.ReconcileSchedulesAsync(["nightly"], CancellationToken.None);
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);

        // Act
        await fixture.Store.MarkTerminalAsync(
            claimed[0].Id, JobRunStatus.Cancelled, null, "nightly", Now, CancellationToken.None);

        // Assert — a crash between the two writes would leave a cancelled occurrence re-runnable
        var run = await fixture.Store.GetRunAsync(Key, CancellationToken.None);
        run!.Status.Should().Be(JobRunStatus.Cancelled);
        var schedule = await fixture.Store.GetScheduleAsync("nightly", CancellationToken.None);
        schedule!.LastCompletedOccurrence.Should().Be(Now);
    }

    [TestMethod]
    public async Task PurgeOnlyTerminalRunsPastRetention()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun("nightly@old", Now.AddDays(-30)), CancellationToken.None);
        await fixture.Store.TryMaterializeAsync(NewRun("nightly@stopped", Now.AddDays(-30)), CancellationToken.None);
        await fixture.Store.TryMaterializeAsync(NewRun("nightly@new", Now), CancellationToken.None);
        var old = await fixture.Store.GetRunAsync("nightly@old", CancellationToken.None);
        var stopped = await fixture.Store.GetRunAsync("nightly@stopped", CancellationToken.None);
        await fixture.Store.MarkTerminalAsync(
            old!.Id, JobRunStatus.Failed, "boom", scheduleName: null, occurrence: null,
            CancellationToken.None);
        await fixture.Store.MarkTerminalAsync(
            stopped!.Id, JobRunStatus.Cancelled, null, scheduleName: null, occurrence: null,
            CancellationToken.None);

        // Act
        var purged = await fixture.Store.PurgeTerminalAsync(Now.AddDays(-7), CancellationToken.None);

        // Assert
        purged.Should().Be(2, "cancelled runs are terminal history too and would otherwise never go");
        (await fixture.Store.GetRunAsync("nightly@new", CancellationToken.None)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task MergeAScopeOnlyWhenTheVersionStillMatches()
    {
        // Arrange — the version guard is what stops two hosts losing one another's merge, and a lost
        // merge here narrows the window rather than merely duplicating work
        await using var database = SharedSqliteDatabase.Create();
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);

        var scope = new JobScope
        {
            ScopeKey = "rebuild@loc-1",
            TriggerName = "rebuild",
            MessageType = "Tests.RecomputeWorkstation",
            Payload = """{"from":"09:00"}""",
            DueAt = Start,
            Version = 0,
        };
        (await store.TryInsertScopeAsync(scope, CancellationToken.None)).Should().BeTrue();

        // Act
        var first = await store.TryMergeScopeAsync(
            "rebuild@loc-1", """{"from":"08:00"}""", version: 0, CancellationToken.None);
        var stale = await store.TryMergeScopeAsync(
            "rebuild@loc-1", """{"from":"10:00"}""", version: 0, CancellationToken.None);

        // Assert — the stale writer is refused and must re-read rather than clobber
        first.Should().BeTrue();
        stale.Should().BeFalse();

        var stored = await store.GetScopeAsync("rebuild@loc-1", CancellationToken.None);
        stored!.Payload.Should().Be("""{"from":"08:00"}""");
        stored.Version.Should().Be(1);
    }

    [TestMethod]
    public async Task LeaveAScopeAloneWhenTheDeleteVersionIsStale()
    {
        // Arrange — pickup deletes conditionally, so a merge landing mid-pickup survives and produces
        // a second run rather than being discarded
        await using var database = SharedSqliteDatabase.Create();
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);
        await store.TryInsertScopeAsync(
            new JobScope
            {
                ScopeKey = "rebuild@loc-1",
                TriggerName = "rebuild",
                MessageType = "Tests.RecomputeWorkstation",
                Payload = """{"from":"09:00"}""",
                DueAt = Start,
                Version = 0,
            },
            CancellationToken.None);
        await store.TryMergeScopeAsync(
            "rebuild@loc-1", """{"from":"08:00"}""", version: 0, CancellationToken.None);

        // Act — pickup read version 0, but a merge has since moved it to 1
        var deleted = await store.TryDeleteScopeAsync("rebuild@loc-1", version: 0, CancellationToken.None);

        // Assert
        deleted.Should().BeFalse();
        (await store.GetScopeAsync("rebuild@loc-1", CancellationToken.None)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task IgnoreASecondInsertOfOneScopeKey()
    {
        // Arrange — two hosts racing to open a scope for one key must produce exactly one row
        await using var database = SharedSqliteDatabase.Create();
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);

        static JobScope Scope(string payload) => new()
        {
            ScopeKey = "rebuild@loc-1",
            TriggerName = "rebuild",
            MessageType = "Tests.RecomputeWorkstation",
            Payload = payload,
            DueAt = Start,
            Version = 0,
        };

        // Act
        var first = await store.TryInsertScopeAsync(Scope("""{"from":"09:00"}"""), CancellationToken.None);
        var second = await store.TryInsertScopeAsync(Scope("""{"from":"10:00"}"""), CancellationToken.None);

        // Assert
        first.Should().BeTrue();
        second.Should().BeFalse();
        (await store.GetScopeAsync("rebuild@loc-1", CancellationToken.None))!
            .Payload.Should().Be("""{"from":"09:00"}""");
    }

    [TestMethod]
    public async Task ReadOnlyScopesThatAreDueAndReportTheNextDueTime()
    {
        // Arrange
        await using var database = SharedSqliteDatabase.Create();
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);

        await store.TryInsertScopeAsync(
            new JobScope
            {
                ScopeKey = "rebuild@now",
                TriggerName = "rebuild",
                MessageType = "Tests.RecomputeWorkstation",
                Payload = "{}",
                DueAt = Start,
                Version = 0,
            },
            CancellationToken.None);
        await store.TryInsertScopeAsync(
            new JobScope
            {
                ScopeKey = "rebuild@later",
                TriggerName = "rebuild",
                MessageType = "Tests.RecomputeWorkstation",
                Payload = "{}",
                DueAt = Start.AddMinutes(30),
                Version = 0,
            },
            CancellationToken.None);

        // Act
        var due = await store.GetDueScopesAsync(Start, batchSize: 32, CancellationToken.None);
        var next = await store.GetNextScopeDueAsync(CancellationToken.None);

        // Assert — the sweep must sleep until the earliest scope, not merely until ClaimTimeout
        due.Should().ContainSingle().Which.ScopeKey.Should().Be("rebuild@now");
        next.Should().Be(Start);
    }

    [TestMethod]
    public void RejectAScopeKeyLongerThanTheIndexableColumn()
    {
        // Arrange — KeyTextType is bounded on the engines that cannot index unbounded text
        var oversized = new string('x', 250);

        // Act
        var act = () => JobScope.ComposeKey("rebuild", oversized);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*200*");
    }

    private static JobRun NewRun(string jobKey, DateTimeOffset runAt) => new()
    {
        JobKey = jobKey,
        ScheduleName = "nightly",
        Occurrence = runAt,
        MessageType = "Conduit.Jobs.Tests.Support.NoopJob",
        Payload = "{}",
        RunAt = runAt,
        Status = JobRunStatus.Pending,
    };
}
