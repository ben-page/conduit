using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;

namespace Conduit.Transport.AmazonSqs.Tests.Unit;

/// <summary>
/// The reaper's prefix, without LocalStack (the reaping itself is <c>Integration.AmazonSqsTopologyReaperShould</c>).
/// </summary>
[TestClass]
public class AmazonSqsTopologyReaperShould
{
    // Resource names are sanitized, so a Developer prefix like john.doe names them john-doe-...: matching the raw
    // prefix, the reaper found nothing to delete
    [TestMethod]
    public void MatchTheSanitizedPrefixOfTheNamesItReaps()
    {
        // Arrange
        var options = new ConduitOptions
        {
            SystemName = "conduit",
            ApplicationName = "orders",
            TopologyMode = TopologyMode.Developer,
            TopologyPrefix = "john.doe",
        };
        var queueName = TopologyNaming.CreateAppResourceName(options, 80);
        var topicName = TopologyNaming.CreateSharedResourceName(options, 256);

        // Act
        var resourcePrefix = AmazonSqsTopologyReaper.ResourceNamePrefix("john.doe");

        // Assert
        resourcePrefix.Should().Be("john-doe");
        queueName.Should().StartWith(resourcePrefix);
        topicName.Should().StartWith(resourcePrefix);
    }
}
