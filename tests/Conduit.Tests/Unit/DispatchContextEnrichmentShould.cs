using System.Security.Claims;
using AwesomeAssertions;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

[TestClass]
public class DispatchContextEnrichmentShould
{
    private sealed class DelegateEnricher(Action<ClaimsPrincipal?, IDictionary<string, object?>> apply)
        : IDispatchContextEnricher
    {
        public void Enrich(ClaimsPrincipal? user, IDictionary<string, object?> properties) =>
            apply(user, properties);
    }

    private static ClaimsPrincipal UserWith(string subClaim)
    {
        var identity = new ClaimsIdentity([new Claim("sub", subClaim)], authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    [TestMethod]
    public void ReturnClientPropertiesUnchangedWhenNoEnrichers()
    {
        // Arrange
        var client = new Dictionary<string, object?> { ["TenantCode"] = "acme" };

        // Act
        var result = DispatchContextEnrichment.Apply([], user: null, client);

        // Assert
        result.Should().BeSameAs(client);
    }

    [TestMethod]
    public void ReturnNullWhenNoEnrichersAndNoClientProperties()
    {
        // Act
        var result = DispatchContextEnrichment.Apply([], user: null, clientProperties: null);

        // Assert
        result.Should().BeNull();
    }

    [TestMethod]
    public void DeriveServerTrustedValueFromAuthenticatedUser()
    {
        // Arrange
        var enricher = new DelegateEnricher((user, props) =>
            props["UserId"] = user?.FindFirst("sub")?.Value);

        // Act
        var result = DispatchContextEnrichment.Apply([enricher], UserWith("emp-99"), clientProperties: null);

        // Assert
        result.Should().NotBeNull();
        result!["UserId"].Should().Be("emp-99");
    }

    [TestMethod]
    public void OverwriteClientSuppliedValueOfSameKey()
    {
        // Arrange — anti-spoofing: enricher writes last and wins.
        var client = new Dictionary<string, object?> { ["UserId"] = "client-spoofed" };
        var enricher = new DelegateEnricher((user, props) => props["UserId"] = "server-trusted");

        // Act
        var result = DispatchContextEnrichment.Apply([enricher], user: null, client);

        // Assert
        result!["UserId"].Should().Be("server-trusted");
    }

    [TestMethod]
    public void NotMutateTheSuppliedClientDictionary()
    {
        // Arrange
        var client = new Dictionary<string, object?> { ["UserId"] = "client" };
        var enricher = new DelegateEnricher((user, props) => props["UserId"] = "server");

        // Act
        DispatchContextEnrichment.Apply([enricher], user: null, client);

        // Assert — original dictionary untouched (a defensive copy was enriched).
        client["UserId"].Should().Be("client");
    }

    [TestMethod]
    public void ComposeMultipleEnrichersInOrder()
    {
        // Arrange
        var first = new DelegateEnricher((user, props) => props["UserId"] = "emp-1");
        var second = new DelegateEnricher((user, props) => props["Tenant"] = "acme");

        // Act
        var result = DispatchContextEnrichment.Apply([first, second], user: null, clientProperties: null);

        // Assert
        result!["UserId"].Should().Be("emp-1");
        result["Tenant"].Should().Be("acme");
    }

    [TestMethod]
    public void PreserveClientPropertiesEnricherDidNotTouch()
    {
        // Arrange
        var client = new Dictionary<string, object?> { ["TenantCode"] = "acme" };
        var enricher = new DelegateEnricher((user, props) => props["UserId"] = "emp-5");

        // Act
        var result = DispatchContextEnrichment.Apply([enricher], user: null, client);

        // Assert
        result!["TenantCode"].Should().Be("acme");
        result["UserId"].Should().Be("emp-5");
    }
}
