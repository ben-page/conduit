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
}
