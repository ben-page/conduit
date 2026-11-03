using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;

namespace Conduit.Jobs.Tests.Unit;

[TestClass]
public class JobScheduleShould
{
    [TestMethod]
    public void ComputeTheNextCronOccurrenceInUtc()
    {
        // Arrange
        var schedule = JobSchedule.FromCron("nightly", "0 2 * * *", TimeZoneInfo.Utc,
            MisfirePolicy.Skip, allowsOverlap: false, maxRuntime: null, () => new NoopJob());
        var after = new DateTimeOffset(2026, 3, 1, 3, 0, 0, TimeSpan.Zero);

        // Act
        var next = schedule.NextOccurrenceAfter(after);

        // Assert
        next.Should().Be(new DateTimeOffset(2026, 3, 2, 2, 0, 0, TimeSpan.Zero));
    }

    [TestMethod]
    public void ComputeTheNextIntervalOccurrence()
    {
        // Arrange
        var schedule = JobSchedule.FromInterval("warm", TimeSpan.FromMinutes(5),
            allowsOverlap: false, maxRuntime: null, () => new NoopJob());
        var after = new DateTimeOffset(2026, 3, 1, 3, 2, 0, TimeSpan.Zero);

        // Act
        var next = schedule.NextOccurrenceAfter(after);

        // Assert
        next.Should().Be(new DateTimeOffset(2026, 3, 1, 3, 7, 0, TimeSpan.Zero));
    }

    [TestMethod]
    public void ComputeNoOccurrenceForAnOnDemandSchedule()
    {
        // Arrange
        var schedule = JobSchedule.FromOnDemand("rederive", allowsOverlap: false, maxRuntime: null, () => new NoopJob());

        // Act — from anywhere on the timeline, the start of time included
        var occurrences = new[]
        {
            DateTimeOffset.MinValue,
            new DateTimeOffset(2026, 3, 1, 3, 2, 0, TimeSpan.Zero),
            DateTimeOffset.MaxValue,
        }.Select(schedule.NextOccurrenceAfter);

        // Assert
        occurrences.Should().AllSatisfy(next => next.Should().BeNull());
        schedule.IsOnDemand.Should().BeTrue();
        schedule.Misfire.Should().Be(MisfirePolicy.Skip);
        schedule.RunsAtStart.Should().BeFalse();
    }

    [TestMethod]
    public void MarkOnlyAnOnDemandScheduleAsOnDemand()
    {
        // Arrange
        var cron = JobSchedule.FromCron("nightly", "0 2 * * *", TimeZoneInfo.Utc,
            MisfirePolicy.Skip, allowsOverlap: false, maxRuntime: null, () => new NoopJob());
        var interval = JobSchedule.FromInterval("warm", TimeSpan.FromMinutes(5),
            allowsOverlap: false, maxRuntime: null, () => new NoopJob());

        // Act
        var flags = new[] { cron.IsOnDemand, interval.IsOnDemand };

        // Assert
        flags.Should().AllBeEquivalentTo(false);
    }

    [TestMethod]
    public void RunOnceAtTheTransitionForTheHourThatDoesNotExistOnASpringForward()
    {
        // Arrange — Europe/London skips 01:00-02:00 on 2026-03-29, so local 01:30 never occurs
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var schedule = JobSchedule.FromCron("daily-0130", "30 1 * * *", london,
            MisfirePolicy.Skip, allowsOverlap: false, maxRuntime: null, () => new NoopJob());
        var after = new DateTimeOffset(2026, 3, 28, 2, 0, 0, TimeSpan.Zero);

        // Act
        var first = schedule.NextOccurrenceAfter(after);
        var second = schedule.NextOccurrenceAfter(first!.Value);

        // Assert — a daily job is not silently dropped on the day its time does not exist: Cronos
        // yields the instant the clock jumps (01:00 UTC / 02:00 BST), so it runs exactly once that
        // day, then resumes at 01:30 local. Naive parsers either skip the day or fire twice.
        first!.Value.Should().Be(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero));
        second!.Value.Should().Be(new DateTimeOffset(2026, 3, 30, 0, 30, 0, TimeSpan.Zero));
    }

    [TestMethod]
    public void RunOnlyOnceForTheRepeatedHourOnAFallBack()
    {
        // Arrange — Europe/London repeats 01:00-02:00 on 2026-10-25, so 01:30 happens twice locally
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var schedule = JobSchedule.FromCron("daily-0130", "30 1 * * *", london,
            MisfirePolicy.Skip, allowsOverlap: false, maxRuntime: null, () => new NoopJob());
        var after = new DateTimeOffset(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

        // Act
        var first = schedule.NextOccurrenceAfter(after);
        var second = schedule.NextOccurrenceAfter(first!.Value);

        // Assert — one occurrence on the 25th, the next is on the 26th
        first!.Value.UtcDateTime.Date.Should().Be(new DateTime(2026, 10, 25));
        second!.Value.UtcDateTime.Date.Should().Be(new DateTime(2026, 10, 26));
    }
}
