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
    public void ReturnExplicitPrefixForDeveloperMode()
    {
        // Arrange
        var options = new ConduitOptions
        {
            TopologyMode = TopologyMode.Developer,
            TopologyPrefix = "custom",
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
    public void ReturnExplicitPrefixForTestMode()
    {
        // Arrange
        var options = new ConduitOptions
        {
            TopologyMode = TopologyMode.Test,
            TopologyPrefix = "shared-prefix",
        };

        // Act
        var prefix = options.TopologyPrefix;

        // Assert
        prefix.Should().Be("shared-prefix");
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
    public void GenerateDifferentPrefixesPerInstance()
    {
        // Arrange
        var options1 = new ConduitOptions { TopologyMode = TopologyMode.Test };
        var options2 = new ConduitOptions { TopologyMode = TopologyMode.Test };

        // Act
        var prefix1 = options1.TopologyPrefix;
        var prefix2 = options2.TopologyPrefix;

        // Assert
        prefix1.Should().NotBe(prefix2);
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
