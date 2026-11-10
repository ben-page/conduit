using AwesomeAssertions;
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

    private const long OneDayAndATick = TimeSpan.TicksPerDay + 1;
}
