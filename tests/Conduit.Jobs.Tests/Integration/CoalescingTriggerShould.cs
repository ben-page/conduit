using System.Text.Json;
using AwesomeAssertions;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Tests.Support;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
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
}
