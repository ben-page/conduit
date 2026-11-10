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
}
