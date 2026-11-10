using AwesomeAssertions;
using Conduit.Audit.Configuration;
using Conduit.Generated;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Conduit.Audit.Tests.Unit;

/// <summary>
/// <c>AddAudit</c> and <c>AddAuditListener</c> refuse options the runtime cannot work with, at registration rather than
/// after the host has started.
/// </summary>
[TestClass]
public sealed class AuditOptionsShould
{
    private const long OneDayAndATick = TimeSpan.TicksPerDay + 1;
    private const long ThreeThousandSixHundredFiftyDaysAndATick = (TimeSpan.TicksPerDay * 3650) + 1;

    [TestMethod]
    [DataRow(nameof(AuditOptions.BatchSize), 0)]
    [DataRow(nameof(AuditOptions.BatchSize), -1)]
    [DataRow(nameof(AuditOptions.ChannelCapacity), 0)]
    [DataRow(nameof(AuditOptions.ChannelCapacity), -1)]
    [DataRow(nameof(AuditOptions.StaleGroupTimeout), 0)]
    [DataRow(nameof(AuditOptions.StaleGroupTimeout), -1)]
    [DataRow(nameof(AuditOptions.ShutdownGrace), -1)]
    [DataRow(nameof(AuditOptions.RetentionPeriod), 0)]
    [DataRow(nameof(AuditOptions.RetentionPeriod), -1)]
    [DataRow(nameof(AuditOptions.PurgeInterval), 0)]
    [DataRow(nameof(AuditOptions.PurgeInterval), -1)]
    public void RejectNonPositiveValues(string option, int value)
    {
        // Arrange — a batch of nothing drains nothing while the channel stays readable (a busy loop), a zero sweep
        // interval throws in PeriodicTimer, a zero purge interval purges in a loop
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit => Register(conduit, option, options => Set(options, option, value)));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain($"AuditOptions.{option}");
    }

    [TestMethod]
    [DataRow(nameof(AuditOptions.StaleGroupTimeout), OneDayAndATick)]
    [DataRow(nameof(AuditOptions.StaleGroupTimeout), long.MaxValue)]
    [DataRow(nameof(AuditOptions.ShutdownGrace), OneDayAndATick)]
    [DataRow(nameof(AuditOptions.ShutdownGrace), long.MaxValue)]
    [DataRow(nameof(AuditOptions.PurgeInterval), OneDayAndATick)]
    [DataRow(nameof(AuditOptions.PurgeInterval), long.MaxValue)]
    [DataRow(nameof(AuditOptions.RetentionPeriod), ThreeThousandSixHundredFiftyDaysAndATick)]
    [DataRow(nameof(AuditOptions.RetentionPeriod), long.MaxValue)]
    public void RefuseADurationAboveItsCeiling(string option, long ticks)
    {
        // Arrange — a timer cannot hold much past 49.7 days, and the purge subtracts the retention from now
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit =>
            Register(conduit, option, options => SetDuration(options, option, TimeSpan.FromTicks(ticks))));

        // Assert
        act.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainSingle().Which.Should().Contain($"AuditOptions.{option}").And.Contain("at most");
    }

    [TestMethod]
    public void AcceptEveryCeilingAndAZeroGraceExactly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddConduit(conduit =>
        {
            conduit.AddAudit(audit =>
            {
                audit.StaleGroupTimeout = TimeSpan.FromDays(1);
                audit.ShutdownGrace = TimeSpan.Zero;
            });
            conduit.AddAuditListener(listener =>
            {
                listener.PurgeInterval = TimeSpan.FromDays(1);
                listener.RetentionPeriod = TimeSpan.FromDays(3650);
            });
        });

        // Assert
        act.Should().NotThrow();
    }

    // The capture options belong to AddAudit, the retention options to AddAuditListener.
    private static void Register(Conduit.Configuration.ConduitBuilder conduit, string option, Action<AuditOptions> set)
    {
        if (option is nameof(AuditOptions.RetentionPeriod) or nameof(AuditOptions.PurgeInterval))
        {
            conduit.AddAuditListener(listener => set(listener.Options));
        }
        else
        {
            conduit.AddAudit(audit => set(audit.Options));
        }
    }

    private static void Set(AuditOptions options, string option, int value)
    {
        switch (option)
        {
            case nameof(AuditOptions.BatchSize):
                options.BatchSize = value;
                break;
            case nameof(AuditOptions.ChannelCapacity):
                options.ChannelCapacity = value;
                break;
            default:
                SetDuration(options, option, TimeSpan.FromSeconds(value));
                break;
        }
    }

    private static void SetDuration(AuditOptions options, string option, TimeSpan value)
    {
        switch (option)
        {
            case nameof(AuditOptions.StaleGroupTimeout):
                options.StaleGroupTimeout = value;
                break;
            case nameof(AuditOptions.ShutdownGrace):
                options.ShutdownGrace = value;
                break;
            case nameof(AuditOptions.RetentionPeriod):
                options.RetentionPeriod = value;
                break;
            case nameof(AuditOptions.PurgeInterval):
                options.PurgeInterval = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(option), option, "Not a duration this test sets.");
        }
    }
}
