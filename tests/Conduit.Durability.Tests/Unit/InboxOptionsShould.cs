using AwesomeAssertions;
using Conduit.Durability.Configuration;
using Conduit.Generated;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public sealed class InboxOptionsShould
{
    private const long OneDayAndATick = TimeSpan.TicksPerDay + 1;

    private const long TenYearsAndATick = (TimeSpan.TicksPerDay * 3650) + 1;

    [TestMethod]
    [DataRow(nameof(InboxOptions.RetentionPeriod), 0)]
    [DataRow(nameof(InboxOptions.RetentionPeriod), -1)]
    [DataRow(nameof(InboxOptions.CleanupInterval), 0)]
    [DataRow(nameof(InboxOptions.CleanupInterval), -1)]
    public void RejectNonPositiveValues(string option, int seconds)
    {
        // Arrange — a record kept for no time dedups nothing; a zero cleanup interval spins, a negative one throws
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddInbox(options => Set(options, option, TimeSpan.FromSeconds(seconds)))));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain($"InboxOptions.{option}");
    }

    [TestMethod]
    [DataRow(nameof(InboxOptions.RetentionPeriod), TenYearsAndATick, "3650.00:00:00")]
    [DataRow(nameof(InboxOptions.RetentionPeriod), long.MaxValue, "3650.00:00:00")]
    [DataRow(nameof(InboxOptions.CleanupInterval), OneDayAndATick, "1.00:00:00")]
    public void RefuseADurationAboveItsCeiling(string option, long ticks, string ceiling)
    {
        // Arrange — the cleanup subtracts the retention from now, which throws near TimeSpan.MaxValue
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddInbox(options => Set(options, option, TimeSpan.FromTicks(ticks)))));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain($"InboxOptions.{option}").And.Contain(ceiling);
    }

    [TestMethod]
    public void AcceptEveryCeilingExactly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddInbox(options =>
            {
                options.RetentionPeriod = TimeSpan.FromDays(3650);
                options.CleanupInterval = TimeSpan.FromDays(1);
            })));

        // Assert
        act.Should().NotThrow();
    }

    private static void Set(InboxOptions options, string option, TimeSpan value)
    {
        switch (option)
        {
            case nameof(InboxOptions.RetentionPeriod):
                options.RetentionPeriod = value;
                break;
            case nameof(InboxOptions.CleanupInterval):
                options.CleanupInterval = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(option), option, "Not an option this test sets.");
        }
    }
}
