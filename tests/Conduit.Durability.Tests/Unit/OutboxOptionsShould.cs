using AwesomeAssertions;
using Conduit.Durability.Configuration;
using Conduit.Generated;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public sealed class OutboxOptionsShould
{
    [TestMethod]
    public void RefuseANegativeInitialBackoff()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options => options.InitialBackoff = TimeSpan.FromSeconds(-1))));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain("InitialBackoff");
    }

    [TestMethod]
    public void RefuseAMaxBackoffBelowTheInitialBackoff()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options =>
            {
                options.InitialBackoff = TimeSpan.FromMinutes(1);
                options.MaxBackoff = TimeSpan.FromSeconds(30);
            })));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain("MaxBackoff");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void RefuseAClaimTimeoutThatIsNotPositive(int seconds)
    {
        // Arrange — a claim that is stale at once would let every relay take over every in-flight entry
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options => options.ClaimTimeout = TimeSpan.FromSeconds(seconds))));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain("ClaimTimeout");
    }

    [TestMethod]
    [DataRow(OneDayAndATick)]
    [DataRow(long.MaxValue)]
    public void RefuseAClaimTimeoutAboveADay(long ticks)
    {
        // Arrange — the claim subtracts the timeout from now, which throws for a value near TimeSpan.MaxValue
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options => options.ClaimTimeout = TimeSpan.FromTicks(ticks))));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain("ClaimTimeout").And.Contain("1.00:00:00");
    }

    [TestMethod]
    [DataRow(OneDayAndATick)]
    [DataRow(long.MaxValue)]
    public void RefuseAMaxBackoffAboveADay(long ticks)
    {
        // Arrange — a failed send adds the backoff to now, which throws for a value near TimeSpan.MaxValue
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options => options.MaxBackoff = TimeSpan.FromTicks(ticks))));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain("MaxBackoff").And.Contain("1.00:00:00");
    }

    [TestMethod]
    public void AcceptAClaimTimeoutAndAMaxBackoffOfExactlyADay()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options =>
            {
                options.ClaimTimeout = TimeSpan.FromDays(1);
                options.MaxBackoff = TimeSpan.FromDays(1);
            })));

        // Assert
        act.Should().NotThrow();
    }

    [TestMethod]
    [DataRow(nameof(OutboxOptions.RelayInterval), 0)]
    [DataRow(nameof(OutboxOptions.RelayInterval), -1)]
    [DataRow(nameof(OutboxOptions.CleanupInterval), 0)]
    [DataRow(nameof(OutboxOptions.CleanupInterval), -1)]
    [DataRow(nameof(OutboxOptions.BatchSize), 0)]
    [DataRow(nameof(OutboxOptions.BatchSize), -1)]
    [DataRow(nameof(OutboxOptions.MaxRetries), 0)]
    [DataRow(nameof(OutboxOptions.MaxRetries), -1)]
    public void RejectNonPositiveValues(string option, int value)
    {
        // Arrange — a negative interval throws in Task.Delay, a zero one spins, and a batch of nothing claims nothing
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options => Set(options, option, value))));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain($"OutboxOptions.{option}");
    }

    [TestMethod]
    public void RefuseASingleAttempt()
    {
        // Arrange — the first failed attempt counts, so one attempt would make every failure final
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options => options.MaxRetries = 1)));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain("MaxRetries").And.Contain("at least 2");
    }

    [TestMethod]
    [DataRow(nameof(OutboxOptions.GracePeriod))]
    [DataRow(nameof(OutboxOptions.CompletedRetention))]
    [DataRow(nameof(OutboxOptions.FailedRetention))]
    public void RefuseANegativeGracePeriodOrRetention(string option)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options => Set(options, option, -1))));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain($"OutboxOptions.{option}");
    }

    [TestMethod]
    [DataRow(nameof(OutboxOptions.RelayInterval), OneDayAndATick, "1.00:00:00")]
    [DataRow(nameof(OutboxOptions.CleanupInterval), OneDayAndATick, "1.00:00:00")]
    [DataRow(nameof(OutboxOptions.GracePeriod), OneDayAndATick, "1.00:00:00")]
    [DataRow(nameof(OutboxOptions.CompletedRetention), TenYearsAndATick, "3650.00:00:00")]
    [DataRow(nameof(OutboxOptions.CompletedRetention), long.MaxValue, "3650.00:00:00")]
    [DataRow(nameof(OutboxOptions.FailedRetention), TenYearsAndATick, "3650.00:00:00")]
    [DataRow(nameof(OutboxOptions.FailedRetention), long.MaxValue, "3650.00:00:00")]
    public void RefuseADurationAboveItsCeiling(string option, long ticks, string ceiling)
    {
        // Arrange — the cleanup subtracts a retention from now and the claim a grace period, both of which throw near
        // TimeSpan.MaxValue; TimeSpan.Zero is what keeps entries forever
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options => SetDuration(options, option, TimeSpan.FromTicks(ticks)))));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain($"OutboxOptions.{option}").And.Contain(ceiling);
    }

    [TestMethod]
    public void AcceptZeroToKeepEntriesForever_AndEveryCeilingExactly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => conduit.AddDurability(durability =>
            durability.AddOutbox(options =>
            {
                options.CompletedRetention = TimeSpan.Zero;
                options.FailedRetention = TimeSpan.FromDays(3650);
                options.GracePeriod = TimeSpan.FromDays(1);
                options.RelayInterval = TimeSpan.FromDays(1);
                options.CleanupInterval = TimeSpan.FromDays(1);
                options.MaxRetries = 2;
                options.BatchSize = 1;
            })));

        // Assert
        act.Should().NotThrow();
    }

    private static void Set(OutboxOptions options, string option, int value)
    {
        switch (option)
        {
            case nameof(OutboxOptions.BatchSize):
                options.BatchSize = value;
                break;
            case nameof(OutboxOptions.MaxRetries):
                options.MaxRetries = value;
                break;
            default:
                SetDuration(options, option, TimeSpan.FromSeconds(value));
                break;
        }
    }

    private static void SetDuration(OutboxOptions options, string option, TimeSpan value)
    {
        switch (option)
        {
            case nameof(OutboxOptions.RelayInterval):
                options.RelayInterval = value;
                break;
            case nameof(OutboxOptions.CleanupInterval):
                options.CleanupInterval = value;
                break;
            case nameof(OutboxOptions.GracePeriod):
                options.GracePeriod = value;
                break;
            case nameof(OutboxOptions.CompletedRetention):
                options.CompletedRetention = value;
                break;
            case nameof(OutboxOptions.FailedRetention):
                options.FailedRetention = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(option), option, "Not a duration this test sets.");
        }
    }

    private const long OneDayAndATick = TimeSpan.TicksPerDay + 1;

    private const long TenYearsAndATick = (TimeSpan.TicksPerDay * 3650) + 1;
}
