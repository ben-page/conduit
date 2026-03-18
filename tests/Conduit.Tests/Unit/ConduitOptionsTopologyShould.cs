using AwesomeAssertions;
using Conduit.Configuration;

namespace Conduit.Tests.Unit;

[TestClass]
public class ConduitOptionsTopologyShould
{
    [TestMethod]
    public void ReturnNullPrefixForProductionMode()
    {
        // Arrange
        var options = new ConduitOptions { TopologyMode = TopologyMode.Production };

        // Act
        var prefix = options.TopologyPrefix;

        // Assert
        prefix.Should().BeNull();
    }

    [TestMethod]
    public void ReturnUserNamePrefixForDeveloperMode()
    {
        // Arrange
        var options = new ConduitOptions { TopologyMode = TopologyMode.Developer };

        // Act
        var prefix = options.TopologyPrefix;

        // Assert
        prefix.Should().Be(Environment.UserName);
    }

    [TestMethod]
    public void ReturnExplicitPrefixForDeveloperModeWhenSet()
    {
        // Arrange
        var options = new ConduitOptions
        {
            TopologyMode = TopologyMode.Developer,
            DeveloperPrefix = "custom",
        };

        // Act
        var prefix = options.TopologyPrefix;

        // Assert
        prefix.Should().Be("custom");
    }

    [TestMethod]
    public void ReturnRandomPrefixForTestMode()
    {
        // Arrange
        var options = new ConduitOptions { TopologyMode = TopologyMode.Test };

        // Act
        var prefix = options.TopologyPrefix;

        // Assert
        prefix.Should().NotBeNull();
        prefix.Should().StartWith("test-");
        prefix!.Length.Should().Be(13);
    }

    [TestMethod]
    public void ReturnSamePrefixOnRepeatedAccess()
    {
        // Arrange
        var options = new ConduitOptions { TopologyMode = TopologyMode.Test };

        // Act
        var first = options.TopologyPrefix;
        var second = options.TopologyPrefix;

        // Assert
        first.Should().Be(second);
    }

    [TestMethod]
    public void DefaultSystemNameToConduit()
    {
        // Arrange
        var options = new ConduitOptions();

        // Assert
        options.SystemName.Should().Be("conduit");
    }

    [TestMethod]
    public void DefaultTopologyModeToProduction()
    {
        // Arrange
        var options = new ConduitOptions();

        // Assert
        options.TopologyMode.Should().Be(TopologyMode.Production);
    }
}
