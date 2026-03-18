using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Tests.Support;

namespace Conduit.Tests.Unit;

[TestClass]
public class TopologyNamingShould
{
    [TestMethod]
    public void CreateSharedResourceName_ProductionMode()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit" };

        // Act
        var name = TopologyNaming.CreateSharedResourceName(options, maxLength: 260);

        // Assert
        name.Should().Be("conduit");
    }

    [TestMethod]
    public void CreateSharedResourceName_DeveloperMode()
    {
        // Arrange
        var options = new ConduitOptions
        {
            SystemName = "conduit",
            TopologyMode = TopologyMode.Developer,
            DeveloperPrefix = "bpage",
        };

        // Act
        var name = TopologyNaming.CreateSharedResourceName(options, maxLength: 260);

        // Assert
        name.Should().Be("bpage-conduit");
    }

    [TestMethod]
    public void CreateSharedResourceName_TestMode()
    {
        // Arrange
        var options = new ConduitOptions
        {
            SystemName = "conduit",
            TopologyMode = TopologyMode.Test,
        };

        // Act
        var name = TopologyNaming.CreateSharedResourceName(options, maxLength: 260);

        // Assert
        name.Should().StartWith("test-");
        name.Should().EndWith("-conduit");
    }

    [TestMethod]
    public void CreateSharedResourceName_WithPlainSuffix()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit" };

        // Act
        var name = TopologyNaming.CreateSharedResourceName(options, "direct", maxLength: 260);

        // Assert
        name.Should().Be("conduit-direct");
    }

    [TestMethod]
    public void CreateAppResourceName_ProductionMode()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit", ApplicationName = "orders" };

        // Act
        var name = TopologyNaming.CreateAppResourceName(options, maxLength: 260);

        // Assert
        name.Should().Be("conduit-orders");
    }

    [TestMethod]
    public void CreateAppResourceName_DeveloperMode()
    {
        // Arrange
        var options = new ConduitOptions
        {
            SystemName = "conduit",
            ApplicationName = "orders",
            TopologyMode = TopologyMode.Developer,
            DeveloperPrefix = "bpage",
        };

        // Act
        var name = TopologyNaming.CreateAppResourceName(options, maxLength: 260);

        // Assert
        name.Should().Be("bpage-conduit-orders");
    }

    [TestMethod]
    public void CreateAppResourceName_WithPlainSuffix_Replies()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit", ApplicationName = "orders" };

        // Act
        var name = TopologyNaming.CreateAppResourceName(options, "replies", maxLength: 260);

        // Assert
        name.Should().Be("conduit-orders-replies");
    }

    [TestMethod]
    public void CreateAppResourceName_WithPlainSuffix_Pool()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit", ApplicationName = "orders" };

        // Act
        var name = TopologyNaming.CreateAppResourceName(options, "bulk", maxLength: 260);

        // Assert
        name.Should().Be("conduit-orders-bulk");
    }

    [TestMethod]
    public void CreateSharedResourceName_WithTypeName_ReversesSegments()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit" };

        // Act
        var name = TopologyNaming.CreateSharedResourceName(options, "Orders.Commands.CreateOrder", maxLength: 50);

        // Assert
        name.Should().Be("CreateOrder-Commands-Orders");
    }

    [TestMethod]
    public void CreateAppResourceName_WithTypeName_IncludesAppName()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit", ApplicationName = "orders" };

        // Act
        var name = TopologyNaming.CreateAppResourceName(options, "Orders.Events.OrderShipped", maxLength: 50);

        // Assert
        name.Should().Be("orders-OrderShipped-Events-Orders");
    }

    [TestMethod]
    public void TruncateAndHashWhenExceedingMaxLength()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit" };
        var longTypeName = "Very.Deep.Namespace.Structure.With.Many.Segments.MyCommand";

        // Act
        var name = TopologyNaming.CreateSharedResourceName(options, longTypeName, maxLength: 50);

        // Assert
        name.Length.Should().BeLessThanOrEqualTo(50);
        name[^9].Should().Be('-');
        name[^8..].Should().MatchRegex("^[0-9a-f]{8}$");
    }

    [TestMethod]
    public void ProduceUniqueHashesForDifferentTypes()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit" };
        var type1 = "Very.Deep.Namespace.Structure.With.Many.Segments.CommandAlpha";
        var type2 = "Very.Deep.Namespace.Structure.With.Many.Segments.CommandBravo";

        // Act
        var name1 = TopologyNaming.CreateSharedResourceName(options, type1, maxLength: 50);
        var name2 = TopologyNaming.CreateSharedResourceName(options, type2, maxLength: 50);

        // Assert
        name1.Should().NotBe(name2);
    }

    [TestMethod]
    public void CreateSharedResourceName_Generic_UsesFullTypeName()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit" };

        // Act
        var name = TopologyNaming.CreateSharedResourceName<SampleCommand>(options, maxLength: 260);

        // Assert
        name.Should().Contain("SampleCommand");
    }

    [TestMethod]
    public void CreateAppResourceName_Generic_IncludesAppName()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit", ApplicationName = "orders" };

        // Act
        var name = TopologyNaming.CreateAppResourceName<SampleCommand>(options, maxLength: 260);

        // Assert
        name.Should().StartWith("orders-");
        name.Should().Contain("SampleCommand");
    }

    [TestMethod]
    public void HandleSingleSegmentTypeName()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit" };

        // Act
        var name = TopologyNaming.CreateSharedResourceName(options, "MyCommand", maxLength: 50);

        // Assert
        name.Should().Be("conduit-MyCommand");
    }

    [TestMethod]
    public void NotExceedMaxLengthForAnyInput()
    {
        // Arrange
        var options = new ConduitOptions
        {
            SystemName = "conduit",
            ApplicationName = "very-long-application-name",
            TopologyMode = TopologyMode.Developer,
            DeveloperPrefix = "longprefix",
        };

        // Act
        var name = TopologyNaming.CreateAppResourceName(
            options, "Some.Very.Long.Namespace.With.Deep.Nesting.MyEvent", maxLength: 50);

        // Assert
        name.Length.Should().BeLessThanOrEqualTo(50);
    }

    [TestMethod]
    public void ThrowWhenApplicationNameIsNullForAppResource()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit" };

        // Act
        var act = () => TopologyNaming.CreateAppResourceName(options, maxLength: 260);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
