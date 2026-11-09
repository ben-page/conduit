using AwesomeAssertions;
using Conduit.Configuration;
using Microsoft.AspNetCore.Builder;

namespace Conduit.Transport.Http.Tests.Unit;

[TestClass]
public class ConduitEndpointBuilderShould
{
    [TestMethod]
    public void NotCollideWithAspNetCoreEndpointBuilder()
    {
        // Arrange
        // Both Conduit.Configuration and Microsoft.AspNetCore.Builder are imported above. The simple
        // name ConduitEndpointBuilder resolves without CS0104; the compile is the real assertion.

        // Act
        var described = Describe(null);

        // Assert
        described.Should().Be("none");
    }

    private static string Describe(ConduitEndpointBuilder? endpoint)
        => endpoint?.EndpointName ?? "none";
}
