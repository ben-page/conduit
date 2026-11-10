using System.Globalization;
using System.IO.Hashing;
using System.Text;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
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
            TopologyPrefix = "bpage",
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
            TopologyPrefix = "bpage",
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
    public void CreateSharedResourceName_WithTypeName_IncludesBaseAndReversesSegments()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit" };

        // Act
        var name = TopologyNaming.CreateSharedResourceName(options, "Orders.Commands.CreateOrder", maxLength: 260);

        // Assert
        name.Should().Be("conduit-CreateOrder-Commands-Orders");
    }

    [TestMethod]
    public void CreateSharedResourceName_WithTypeName_DeveloperMode_IncludesPrefix()
    {
        // Arrange
        var options = new ConduitOptions
        {
            SystemName = "conduit",
            TopologyMode = TopologyMode.Developer,
            TopologyPrefix = "bpage",
        };

        // Act
        var name = TopologyNaming.CreateSharedResourceName(options, "Orders.Commands.CreateOrder", maxLength: 260);

        // Assert
        name.Should().Be("bpage-conduit-CreateOrder-Commands-Orders");
    }

    [TestMethod]
    public void CreateAppResourceName_WithTypeName_IncludesBaseAndAppName()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit", ApplicationName = "orders" };

        // Act
        var name = TopologyNaming.CreateAppResourceName(options, "Orders.Events.OrderShipped", maxLength: 260);

        // Assert
        name.Should().Be("conduit-orders-OrderShipped-Events-Orders");
    }

    [TestMethod]
    public void CreateAppResourceName_WithTypeName_DeveloperMode_IncludesPrefix()
    {
        // Arrange
        var options = new ConduitOptions
        {
            SystemName = "conduit",
            ApplicationName = "orders",
            TopologyMode = TopologyMode.Developer,
            TopologyPrefix = "bpage",
        };

        // Act
        var name = TopologyNaming.CreateAppResourceName(options, "Orders.Events.OrderShipped", maxLength: 260);

        // Assert
        name.Should().Be("bpage-conduit-orders-OrderShipped-Events-Orders");
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
    public void KeepLongSiblingNamesDistinct()
    {
        // Arrange — two applications whose names agree on everything a 50-character limit keeps,
        // subscribed to one message type: only a hash of the whole name tells their queues apart
        var alpha = new ConduitOptions { SystemName = "conduit", ApplicationName = "fulfilment-warehouse-dispatcher-service-alpha" };
        var bravo = new ConduitOptions { SystemName = "conduit", ApplicationName = "fulfilment-warehouse-dispatcher-service-bravo" };
        const string messageType = "Orders.Events.OrderShipped";

        // Act
        var alphaName = TopologyNaming.CreateAppResourceName(alpha, messageType, maxLength: 50);
        var bravoName = TopologyNaming.CreateAppResourceName(bravo, messageType, maxLength: 50);

        // Assert — the kept 41 characters are shared, the hash of the full name is not
        alphaName.Should().NotBe(bravoName);
        alphaName[..41].Should().Be(bravoName[..41]);
        var fullName = "conduit-fulfilment-warehouse-dispatcher-service-alpha-OrderShipped-Events-Orders";
        var fullNameHash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(fullName))
            .ToString("x16", CultureInfo.InvariantCulture)[..8];
        alphaName.Should().Be($"{fullName[..41]}-{fullNameHash}");
    }

    [TestMethod]
    public void SanitizeNestedTypesAndWindowsUserNames()
    {
        // Arrange — a Windows account as the developer prefix, a dotted application name and a
        // nested message type: '\', '.' and '+' are each outside [A-Za-z0-9_-]
        var options = new ConduitOptions
        {
            SystemName = "conduit",
            ApplicationName = "Billing.Api",
            TopologyMode = TopologyMode.Developer,
            TopologyPrefix = @"CORP\jdoe",
        };

        // Act
        var sharedName = TopologyNaming.CreateSharedResourceName(options, "Orders.Commands.Outer+Inner", maxLength: 260);
        var appName = TopologyNaming.CreateAppResourceName(options, maxLength: 260);

        // Assert
        sharedName.Should().Be("CORP-jdoe-conduit-Outer-Inner-Commands-Orders");
        appName.Should().Be("CORP-jdoe-conduit-Billing-Api");
    }

    [TestMethod]
    [DataRow("replies")]
    [DataRow("REPLIES")]
    [DataRow("Dead-Letter")]
    [DataRow("dead letter")]
    public void RejectReservedPoolNames(string poolName)
    {
        // Arrange — a pool's queue is named like the reply queue or the dead-letter queue the
        // transport creates beside it ('dead letter' sanitizes to 'dead-letter')
        var options = new FakeBrokerOptions();

        // Act
        var act = () => options.AddPool(poolName, pool => pool.Concurrency = 2);

        // Assert
        act.Should().Throw<ArgumentException>().WithParameterName("name");
        options.Pools.Should().BeEmpty();
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
    public void CreateAppResourceName_Generic_IncludesBaseAndAppName()
    {
        // Arrange
        var options = new ConduitOptions { SystemName = "conduit", ApplicationName = "orders" };

        // Act
        var name = TopologyNaming.CreateAppResourceName<SampleCommand>(options, maxLength: 260);

        // Assert
        name.Should().StartWith("conduit-orders-");
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
            TopologyPrefix = "longprefix",
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

    [TestMethod]
    public void TestMode_TopologyPrefix_IsStableAcrossMultipleAccesses()
    {
        // Arrange
        var options = new ConduitOptions
        {
            SystemName = "conduit",
            TopologyMode = TopologyMode.Test,
        };

        // Act — access prefix multiple times
        var name1 = TopologyNaming.CreateSharedResourceName(options, maxLength: 260);
        var name2 = TopologyNaming.CreateSharedResourceName(options, maxLength: 260);

        // Assert — same options instance should produce the same prefix
        name1.Should().Be(name2);
    }

    [TestMethod]
    public void TestMode_TopologyPrefix_DiffersPerOptionsInstance()
    {
        // Arrange
        var options1 = new ConduitOptions
        {
            SystemName = "conduit",
            TopologyMode = TopologyMode.Test,
        };
        var options2 = new ConduitOptions
        {
            SystemName = "conduit",
            TopologyMode = TopologyMode.Test,
        };

        // Act
        var name1 = TopologyNaming.CreateSharedResourceName(options1, maxLength: 260);
        var name2 = TopologyNaming.CreateSharedResourceName(options2, maxLength: 260);

        // Assert — different options instances should produce different prefixes
        name1.Should().NotBe(name2);
    }
}
