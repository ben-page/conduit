using AwesomeAssertions;
using Conduit.Jobs;
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
        await fixture.Store.CompleteAsync(claimed[0], claimed[0].ClaimToken!, "nightly", Now, retainRunKey: false, Now, CancellationToken.None);

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
        var recorded = await fixture.Store.IncrementAttemptAsync(
            claimed[0].Id, claimed[0].ClaimToken!, Now.AddMinutes(1), "boom", CancellationToken.None);

        // Assert — the previous occurrence has not succeeded, so a second must not start
        recorded.Should().BeTrue();
        (await fixture.Store.GetRunAsync(Key, CancellationToken.None))!.AttemptCount.Should().Be(1);
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
            claimed[0], claimed[0].ClaimToken!, JobRunStatus.Failed, "gave up", scheduleName: null, occurrence: null, Now,
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
            claimed[0], claimed[0].ClaimToken!, JobRunStatus.Failed, null, scheduleName: null, occurrence: null, Now,
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
    public async Task NotCompleteARunAnotherHostHolds()
    {
        // Arrange — worker-a's claim went stale and worker-b took the run over
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.ReconcileSchedulesAsync(["nightly"], CancellationToken.None);
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var original = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);
        var stolen = await fixture.Store.ClaimDueAsync("worker-b", Now.AddHours(1), 10, CancellationToken.None);

        // Act — worker-a's job finishes after all, and it records success
        var recorded = await fixture.Store.CompleteAsync(
            original[0], "worker-a", "nightly", Now, retainRunKey: false, Now.AddHours(1), CancellationToken.None);

        // Assert — the row, the cursor and the history are worker-b's to write; deleting the row would
        // let a late replica re-materialize the occurrence while worker-b is still running it
        recorded.Should().BeFalse();
        var run = await fixture.Store.GetRunAsync(Key, CancellationToken.None);
        run!.Status.Should().Be(JobRunStatus.Claiming);
        run.ClaimToken.Should().Be(stolen[0].ClaimToken);
        (await fixture.Store.GetScheduleAsync("nightly", CancellationToken.None))!
            .LastCompletedOccurrence.Should().BeNull();
        (await fixture.Store.QueryHistoryAsync(new JobHistoryFilter { JobKey = Key }, CancellationToken.None))
            .Entries.Should().BeEmpty();
    }

    [TestMethod]
    public async Task NotMarkTerminalARunAnotherHostHolds()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.ReconcileSchedulesAsync(["nightly"], CancellationToken.None);
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var original = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);
        var stolen = await fixture.Store.ClaimDueAsync("worker-b", Now.AddHours(1), 10, CancellationToken.None);

        // Act — worker-a's last attempt fails
        var recorded = await fixture.Store.MarkTerminalAsync(
            original[0], "worker-a", JobRunStatus.Failed, "boom", "nightly", Now, Now.AddHours(1),
            CancellationToken.None);

        // Assert — a Failed row would end worker-b's run as far as every other host could tell
        recorded.Should().BeFalse();
        var run = await fixture.Store.GetRunAsync(Key, CancellationToken.None);
        run!.Status.Should().Be(JobRunStatus.Claiming);
        run.ClaimToken.Should().Be(stolen[0].ClaimToken);
        (await fixture.Store.GetScheduleAsync("nightly", CancellationToken.None))!
            .LastCompletedOccurrence.Should().BeNull();
        (await fixture.Store.QueryHistoryAsync(new JobHistoryFilter { JobKey = Key }, CancellationToken.None))
            .Entries.Should().BeEmpty();
    }

    [TestMethod]
    public async Task NotCountAnAttemptOnARunAnotherHostHolds()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var original = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);
        var stolen = await fixture.Store.ClaimDueAsync("worker-b", Now.AddHours(1), 10, CancellationToken.None);

        // Act — worker-a's attempt fails, with attempts left
        var recorded = await fixture.Store.IncrementAttemptAsync(
            original[0].Id, "worker-a", Now.AddHours(2), "boom", CancellationToken.None);

        // Assert — released to Pending, the run would be claimed and run a third time beside worker-b
        recorded.Should().BeFalse();
        var run = await fixture.Store.GetRunAsync(Key, CancellationToken.None);
        run!.Status.Should().Be(JobRunStatus.Claiming);
        run.ClaimToken.Should().Be(stolen[0].ClaimToken);
        run.AttemptCount.Should().Be(0);
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
            claimed[0], claimed[0].ClaimToken!, JobRunStatus.Cancelled, null, "nightly", Now, Now, CancellationToken.None);

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
        var old = await fixture.Store.InsertClaimedAsync(
            NewRun("nightly@old", Now.AddDays(-30)), "worker-old", Now, CancellationToken.None);
        var stopped = await fixture.Store.InsertClaimedAsync(
            NewRun("nightly@stopped", Now.AddDays(-30)), "worker-stopped", Now, CancellationToken.None);
        await fixture.Store.TryMaterializeAsync(NewRun("nightly@new", Now), CancellationToken.None);
        await fixture.Store.MarkTerminalAsync(
            old!, "worker-old", JobRunStatus.Failed, "boom", scheduleName: null, occurrence: null, Now,
            CancellationToken.None);
        await fixture.Store.MarkTerminalAsync(
            stopped!, "worker-stopped", JobRunStatus.Cancelled, null, scheduleName: null, occurrence: null, Now,
            CancellationToken.None);

        // Act
        var purged = await fixture.Store.PurgeTerminalAsync(Now.AddDays(-7), CancellationToken.None);

        // Assert
        purged.Should().Be(2, "cancelled runs are terminal history too and would otherwise never go");
        (await fixture.Store.GetRunAsync("nightly@new", CancellationToken.None)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task KeepACompletedRunsKeyWhenAskedTo()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);

        // Act
        await fixture.Store.CompleteAsync(
            claimed[0], claimed[0].ClaimToken!, null, null, retainRunKey: true, Now, CancellationToken.None);

        // Assert — the key keeps refusing both write paths, which is what collapses a redelivery
        // arriving after the run finished, while nothing that looks for live work sees the row
        var kept = await fixture.Store.GetRunAsync(Key, CancellationToken.None);
        kept!.Status.Should().Be(JobRunStatus.Completed);
        kept.ClaimToken.Should().BeNull();
        (await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None)).Should().BeFalse();
        (await fixture.Store.InsertClaimedAsync(NewRun(Key, Now), "host-b", Now, CancellationToken.None))
            .Should().BeNull();
        (await fixture.Store.ClaimDueAsync("worker-b", Now, 10, CancellationToken.None)).Should().BeEmpty();
        (await fixture.Store.HasOutstandingAsync("nightly", CancellationToken.None)).Should().BeFalse();
    }

    [TestMethod]
    public async Task PurgeOnlyCompletedRunsPastTheirOwnRetention()
    {
        // Arrange — two kept completions either side of the cutoff, and a failed run older than both
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun("event@old", Now.AddDays(-3)), CancellationToken.None);
        await fixture.Store.TryMaterializeAsync(NewRun("event@new", Now), CancellationToken.None);
        var failed = await fixture.Store.InsertClaimedAsync(
            NewRun("event@failed", Now.AddDays(-30)), "worker-failed", Now, CancellationToken.None);
        await fixture.Store.MarkTerminalAsync(
            failed!, "worker-failed", JobRunStatus.Failed, "boom", scheduleName: null, occurrence: null, Now,
            CancellationToken.None);
        foreach (var run in await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None))
        {
            await fixture.Store.CompleteAsync(run, run.ClaimToken!, null, null, retainRunKey: true, Now, CancellationToken.None);
        }

        // Act
        var purged = await fixture.Store.PurgeCompletedAsync(Now.AddDays(-1), CancellationToken.None);

        // Assert — the failed row answers to FailedRetention, not to this purge
        purged.Should().Be(1);
        (await fixture.Store.GetRunAsync("event@old", CancellationToken.None)).Should().BeNull();
        (await fixture.Store.GetRunAsync("event@new", CancellationToken.None)).Should().NotBeNull();
        (await fixture.Store.GetRunAsync("event@failed", CancellationToken.None)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task ClearATerminalRunHeldByAKey()
    {
        // Arrange — a coalescing trigger's run key is its coalesce key, so a Failed row must not be
        // able to hold that key until the retention purge; the scope waiting behind it is different work
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);
        await fixture.Store.MarkTerminalAsync(
            claimed[0], claimed[0].ClaimToken!, JobRunStatus.Failed, "boom", scheduleName: null, occurrence: null, Now,
            CancellationToken.None);

        // Act
        var cleared = await fixture.Store.TryClearTerminalRunAsync(Key, CancellationToken.None);

        // Assert
        cleared.Should().BeTrue();
        (await fixture.Store.GetRunAsync(Key, CancellationToken.None)).Should().BeNull();
    }

    [TestMethod]
    public async Task RefuseToClearAPendingRun()
    {
        // Arrange — only a row that has stopped attempting may be cleared; a pending or claimed run is
        // live work and must never be deleted out from under it
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);

        // Act
        var cleared = await fixture.Store.TryClearTerminalRunAsync(Key, CancellationToken.None);

        // Assert
        cleared.Should().BeFalse();
        (await fixture.Store.GetRunAsync(Key, CancellationToken.None)).Should().NotBeNull();
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
            ScopeId = "gen-1",
            TriggerName = "rebuild",
            MessageType = "Tests.RecomputeWorkstation",
            Payload = """{"from":"09:00"}""",
            DueAt = Start,
            Version = 0,
        };
        (await store.TryInsertScopeAsync(scope, CancellationToken.None)).Should().BeTrue();

        // Act
        var first = await store.TryMergeScopeAsync(
            "rebuild@loc-1", "gen-1", """{"from":"08:00"}""", version: 0, CancellationToken.None);
        var stale = await store.TryMergeScopeAsync(
            "rebuild@loc-1", "gen-1", """{"from":"10:00"}""", version: 0, CancellationToken.None);

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
                ScopeId = "gen-1",
                TriggerName = "rebuild",
                MessageType = "Tests.RecomputeWorkstation",
                Payload = """{"from":"09:00"}""",
                DueAt = Start,
                Version = 0,
            },
            CancellationToken.None);
        await store.TryMergeScopeAsync(
            "rebuild@loc-1", "gen-1", """{"from":"08:00"}""", version: 0, CancellationToken.None);

        // Act — pickup read version 0, but a merge has since moved it to 1
        var deleted = await store.TryDeleteScopeAsync(
            "rebuild@loc-1", "gen-1", version: 0, CancellationToken.None);

        // Assert
        deleted.Should().BeFalse();
        (await store.GetScopeAsync("rebuild@loc-1", CancellationToken.None)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task RefuseAMergeAimedAtAReplacedScopeGeneration()
    {
        // Arrange — a scope's Version resets to 0 when the row is deleted and reopened, so a merge
        // that straddles a pickup would otherwise match the row that REPLACED its target and clobber
        // it. That narrows the scope, which loses work rather than duplicating it.
        await using var database = SharedSqliteDatabase.Create();
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);

        static JobScope Generation(string scopeId, string payload) => new()
        {
            ScopeKey = "rebuild@loc-1",
            ScopeId = scopeId,
            TriggerName = "rebuild",
            MessageType = "Tests.RecomputeWorkstation",
            Payload = payload,
            DueAt = Start,
            Version = 0,
        };

        await store.TryInsertScopeAsync(Generation("first", """{"from":"09:00"}"""), CancellationToken.None);
        await store.TryDeleteScopeAsync("rebuild@loc-1", "first", version: 0, CancellationToken.None);
        await store.TryInsertScopeAsync(Generation("second", """{"from":"11:00"}"""), CancellationToken.None);

        // Act — a merge still holding the first generation's identity, at the same version number
        var merged = await store.TryMergeScopeAsync(
            "rebuild@loc-1", "first", """{"from":"08:00"}""", version: 0, CancellationToken.None);

        // Assert — refused, and the live generation's payload is untouched
        merged.Should().BeFalse();
        (await store.GetScopeAsync("rebuild@loc-1", CancellationToken.None))!
            .Payload.Should().Be("""{"from":"11:00"}""");
    }

    [TestMethod]
    public async Task IgnoreASecondInsertOfOneScopeKey()
    {
        // Arrange — two hosts racing to open a scope for one key must produce exactly one row
        await using var database = SharedSqliteDatabase.Create();
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);

        static JobScope Scope(string scopeId, string payload) => new()
        {
            ScopeKey = "rebuild@loc-1",
            ScopeId = scopeId,
            TriggerName = "rebuild",
            MessageType = "Tests.RecomputeWorkstation",
            Payload = payload,
            DueAt = Start,
            Version = 0,
        };

        // Act
        var first = await store.TryInsertScopeAsync(Scope("first", """{"from":"09:00"}"""), CancellationToken.None);
        var second = await store.TryInsertScopeAsync(Scope("second", """{"from":"10:00"}"""), CancellationToken.None);

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
                ScopeId = "gen-now",
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
                ScopeId = "gen-later",
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

    [TestMethod]
    public async Task WriteACompletedHistoryRowOnSuccess()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.ReconcileSchedulesAsync(["nightly"], CancellationToken.None);
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);
        var completedAt = Now.AddMinutes(1);

        // Act
        await fixture.Store.CompleteAsync(claimed[0], claimed[0].ClaimToken!, "nightly", Now, retainRunKey: false, completedAt, CancellationToken.None);

        // Assert — the outcome survives even though CompleteAsync just deleted the ledger row
        var page = await fixture.Store.QueryHistoryAsync(
            new JobHistoryFilter { JobKey = Key }, CancellationToken.None);
        var entry = page.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(JobRunOutcome.Completed);
        entry.ScheduleName.Should().Be("nightly");
        entry.CompletedAt.Should().Be(completedAt);
        entry.StartedAt.Should().NotBeNull();
        entry.LastError.Should().BeNull();
    }

    [TestMethod]
    public async Task WriteAFailedHistoryRowWhenAttemptsAreExhausted()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);
        var completedAt = Now.AddMinutes(1);

        // Act
        await fixture.Store.MarkTerminalAsync(
            claimed[0], claimed[0].ClaimToken!, JobRunStatus.Failed, "boom", scheduleName: null, occurrence: null, completedAt,
            CancellationToken.None);

        // Assert
        var page = await fixture.Store.QueryHistoryAsync(
            new JobHistoryFilter { JobKey = Key }, CancellationToken.None);
        var entry = page.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(JobRunOutcome.Failed);
        entry.LastError.Should().Be("boom");
        entry.CompletedAt.Should().Be(completedAt);
    }

    [TestMethod]
    public async Task KeepHistoryAfterTheLedgerRowIsSuperseded()
    {
        // Arrange — the exact scenario TryClearTerminalRunAsync exists for: a coalescing pickup
        // deletes the ledger row to free the key, and the failure it once held must not go with it
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun(Key, Now), CancellationToken.None);
        var claimed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);
        await fixture.Store.MarkTerminalAsync(
            claimed[0], claimed[0].ClaimToken!, JobRunStatus.Failed, "boom", scheduleName: null, occurrence: null, Now,
            CancellationToken.None);

        // Act
        (await fixture.Store.TryClearTerminalRunAsync(Key, CancellationToken.None)).Should().BeTrue();

        // Assert — the ledger row is gone, exactly as before, but history still has the outcome
        (await fixture.Store.GetRunAsync(Key, CancellationToken.None)).Should().BeNull();
        var page = await fixture.Store.QueryHistoryAsync(
            new JobHistoryFilter { JobKey = Key }, CancellationToken.None);
        page.Entries.Should().ContainSingle().Which.Outcome.Should().Be(JobRunOutcome.Failed);
    }

    [TestMethod]
    public async Task FilterHistoryByOutcome()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun("nightly@a", Now), CancellationToken.None);
        var failed = await fixture.Store.ClaimDueAsync("worker-a", Now, 10, CancellationToken.None);
        await fixture.Store.MarkTerminalAsync(
            failed[0], failed[0].ClaimToken!, JobRunStatus.Failed, "boom", scheduleName: null, occurrence: null, Now,
            CancellationToken.None);

        await fixture.Store.TryMaterializeAsync(NewRun("nightly@b", Now), CancellationToken.None);
        var completed = await fixture.Store.ClaimDueAsync("worker-b", Now, 10, CancellationToken.None);
        await fixture.Store.CompleteAsync(completed[0], completed[0].ClaimToken!, null, null, retainRunKey: false, Now, CancellationToken.None);

        // Act
        var page = await fixture.Store.QueryHistoryAsync(
            new JobHistoryFilter { Outcome = JobRunOutcome.Failed }, CancellationToken.None);

        // Assert
        page.Entries.Should().ContainSingle().Which.JobKey.Should().Be("nightly@a");
    }

    [TestMethod]
    public async Task PageHistoryResultsNewestOutcomeFirst()
    {
        // Arrange — three completed runs, each reaching its outcome a minute apart
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        for (var i = 0; i < 3; i++)
        {
            var key = $"nightly@run-{i}";
            await fixture.Store.TryMaterializeAsync(NewRun(key, Now), CancellationToken.None);
            var claimed = await fixture.Store.ClaimDueAsync($"worker-{i}", Now, 10, CancellationToken.None);
            await fixture.Store.CompleteAsync(claimed[0], claimed[0].ClaimToken!, null, null, retainRunKey: false, Now.AddMinutes(i), CancellationToken.None);
        }

        // Act — page size 2, so the oldest entry needs a second page
        var firstPage = await fixture.Store.QueryHistoryAsync(
            new JobHistoryFilter { PageSize = 2 }, CancellationToken.None);
        var secondPage = await fixture.Store.QueryHistoryAsync(
            new JobHistoryFilter { PageSize = 2, PageToken = firstPage.NextPageToken }, CancellationToken.None);

        // Assert — newest CompletedAt first, and the cursor reaches every row exactly once
        firstPage.TotalCount.Should().Be(3);
        firstPage.Entries.Should().HaveCount(2);
        firstPage.Entries[0].JobKey.Should().Be("nightly@run-2", "newest outcome first");
        firstPage.NextPageToken.Should().NotBeNull();
        secondPage.Entries.Should().ContainSingle().Which.JobKey.Should().Be("nightly@run-0");
        secondPage.NextPageToken.Should().BeNull();
    }

    [TestMethod]
    public async Task PurgeOnlyHistoryPastRetention()
    {
        // Arrange
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        await fixture.Store.TryMaterializeAsync(NewRun("nightly@old", Now), CancellationToken.None);
        var old = await fixture.Store.ClaimDueAsync("worker-old", Now, 10, CancellationToken.None);
        await fixture.Store.CompleteAsync(old[0], old[0].ClaimToken!, null, null, retainRunKey: false, Now.AddDays(-30), CancellationToken.None);

        await fixture.Store.TryMaterializeAsync(NewRun("nightly@new", Now), CancellationToken.None);
        var recent = await fixture.Store.ClaimDueAsync("worker-new", Now, 10, CancellationToken.None);
        await fixture.Store.CompleteAsync(recent[0], recent[0].ClaimToken!, null, null, retainRunKey: false, Now, CancellationToken.None);

        // Act
        var purged = await fixture.Store.PurgeHistoryAsync(Now.AddDays(-7), CancellationToken.None);

        // Assert
        purged.Should().Be(1);
        var remaining = await fixture.Store.QueryHistoryAsync(new JobHistoryFilter(), CancellationToken.None);
        remaining.Entries.Should().ContainSingle().Which.JobKey.Should().Be("nightly@new");
    }

    [TestMethod]
    public async Task OnlyEverMoveTheCursorForward()
    {
        // Arrange — two overlapping runs, the later occurrence finishing first; the instants straddle a
        // rollover of every field, so text order has to be chronological order
        await using var fixture = await SqliteJobStoreFixture.CreateAsync();
        var store = fixture.Store;
        await store.ReconcileSchedulesAsync(["nightly"], CancellationToken.None);
        var earlier = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999);
        var later = earlier.AddTicks(1);
        await store.TryMaterializeAsync(NewRun("nightly@earlier", earlier), CancellationToken.None);
        await store.TryMaterializeAsync(NewRun("nightly@later", later), CancellationToken.None);
        var claimed = await store.ClaimDueAsync("worker", later, 10, CancellationToken.None);

        // Act — later completes, then earlier completes, a misfire skip names a past occurrence
        await store.CompleteAsync(
            claimed.Single(run => run.JobKey == "nightly@later"), "worker", "nightly", later, retainRunKey: false,
            later, CancellationToken.None);
        await store.MarkTerminalAsync(
            claimed.Single(run => run.JobKey == "nightly@earlier"), "worker", JobRunStatus.Failed, "late",
            "nightly", earlier, later, CancellationToken.None);
        await store.AdvanceCursorAsync("nightly", earlier, CancellationToken.None);

        // Assert — moved back, a replica late for the later occurrence would find neither its row nor
        // a cursor past it, and run it again
        (await store.GetScheduleAsync("nightly", CancellationToken.None))!
            .LastCompletedOccurrence.Should().Be(later);
        (await store.TryMaterializeWithoutOverlapAsync(NewRun("nightly@later", later), CancellationToken.None))
            .Should().Be(MaterializeOutcome.AlreadyCompleted);
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
