using AwesomeAssertions;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;

namespace Conduit.Jobs.Tests.Unit;

[TestClass]
public class ManualRunKeyShould
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 21, 31, 4, TimeSpan.Zero);

    [TestMethod]
    public void BuildTheScheduleNameTheMarkerAndTheInstant()
    {
        // Act
        var key = ManualRunKey.Build("warm", At);

        // Assert
        key.Should().Be("warm@manual-2026-11-03T21:31:04.0000000Z");
    }

    [TestMethod]
    public void ReadItsOwnKeyAsManual()
    {
        // Act
        var manual = ManualRunKey.Is(ManualRunKey.Build("warm", At), "warm");

        // Assert
        manual.Should().BeTrue();
    }

    [TestMethod]
    public void ReadAnOccurrenceAndAStartUpRunAsNotManual()
    {
        // Act
        var occurrence = ManualRunKey.Is(DurableJobScheduler.BuildJobKey("warm", At), "warm");
        var startUp = ManualRunKey.Is(DurableJobScheduler.BuildStartUpJobKey("warm", At), "warm");

        // Assert
        occurrence.Should().BeFalse();
        startUp.Should().BeFalse();
    }

    [TestMethod]
    public void ReadAManualKeyOfAnotherScheduleAsNotManual()
    {
        // Act — "warm" is a prefix of "warmer", so the marker has to follow the name exactly
        var other = ManualRunKey.Is(ManualRunKey.Build("warmer", At), "warm");
        var unnamed = ManualRunKey.Is(ManualRunKey.Build("warm", At), scheduleName: null);

        // Assert
        other.Should().BeFalse();
        unnamed.Should().BeFalse();
    }

    [TestMethod]
    public void MarkTheRecordsFromTheirKey()
    {
        // Arrange
        var key = ManualRunKey.Build("warm", At);

        // Act
        var run = new JobRunInfo(key, "warm", "T", At, JobRunStatus.Pending, 0, null, null, false);
        var entry = new JobRunHistoryInfo(key, "warm", "T", JobRunOutcome.Completed, At, At, At, 1, null);
        var occurrence = run with { JobKey = DurableJobScheduler.BuildJobKey("warm", At) };

        // Assert
        run.IsManual.Should().BeTrue();
        entry.IsManual.Should().BeTrue();
        occurrence.IsManual.Should().BeFalse();
    }
}
