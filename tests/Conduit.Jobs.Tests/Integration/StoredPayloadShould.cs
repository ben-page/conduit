using AwesomeAssertions;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

/// <summary>
/// A job stored before a member was added to its command must keep running: the job read path uses the lenient
/// shared options, not the strict ones an inbound wire request is read with (ContractMismatch).
/// </summary>
[TestClass]
public class StoredPayloadShould
{
    [TestMethod]
    public async Task RunAJobWhosePayloadIsMissingAMember()
    {
        // Arrange -- a stored run written before RecomputeWorkstation gained "from"
        var spy = new WorkstationSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.On<PunchChanged>("rebuild")
                .Run(punch => new RecomputeWorkstation(punch.LocationId, punch.From)),
            timeProvider: new FakeTimeProvider(new DateTimeOffset(2026, 8, 26, 1, 59, 0, TimeSpan.Zero)),
            connectionString: database.ConnectionString,
            workstationSpy: spy);
        var run = new JobRun
        {
            JobKey = "rebuild@legacy",
            MessageType = typeof(RecomputeWorkstation).FullName!,
            Payload = """{"locationId":"loc-1"}""",
        };

        // Act -- through the real job read path
        var attempt = await host.GetRequiredService<JobRunner>()
            .ExecuteAsync(run, () => false, CancellationToken.None, CancellationToken.None);

        // Assert
        attempt.Outcome.Should().Be(JobOutcome.Completed);
        spy.Rebuilds.Should().ContainSingle().Which.LocationId.Should().Be("loc-1");
    }
}
