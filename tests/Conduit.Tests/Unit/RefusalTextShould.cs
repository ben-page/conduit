using System.Text.Json;
using AwesomeAssertions;
using Conduit.Exceptions;
using Conduit.Runtime;
using Conduit.Tests.Support;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class RefusalTextShould
{
    [TestMethod]
    public void StayOffTheClientData()
    {
        // Arrange
        var refusal = new PreflightRefusal("x") { Text = new RefusalText("orders.shipped") };

        // Act
        var properties = ConduitException.ClientDataProperties(refusal.GetType()).Select(p => p.Name);

        // Assert
        properties.Should().NotContain(nameof(ConduitException.Text));
    }

    [TestMethod]
    public void StayOffTheWireOnADetail()
    {
        // Arrange
        var detail = new DispatchErrorDetail("eTag", "missing") { Text = new RefusalText("field.missing") };

        // Act
        var json = JsonSerializer.Serialize(detail, ConduitRuntime.JsonOptions);

        // Assert
        json.Should().Be("""{"field":"eTag","message":"missing"}""");
    }

    [TestMethod]
    public void NameTheContractMismatchSentence()
    {
        // Arrange & Act
        var refusal = new ContractMismatchException(["eTag", "name"], total: 2);

        // Assert
        refusal.Text!.Name.Should().Be(ConduitTextNames.ContractMismatch);
        refusal.Text.Values!["members"].Should().Be("eTag, name");
        refusal.Message.Should().Be("This page is out of date: the request is missing eTag, name. Reload to continue.");
    }

    [TestMethod]
    public void NameTheContractMismatchSentenceWithMore()
    {
        // Arrange & Act
        var refusal = new ContractMismatchException(["eTag"], total: 56);

        // Assert
        refusal.Text!.Name.Should().Be(ConduitTextNames.ContractMismatchMore);
        refusal.Text.Values!["members"].Should().Be("eTag");
        refusal.Text.Values["more"].Should().Be(55);
    }
}
