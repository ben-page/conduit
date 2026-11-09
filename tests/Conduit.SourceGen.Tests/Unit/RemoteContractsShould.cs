using System.Globalization;
using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class RemoteContractsShould : GeneratorTestBase
{
    [TestMethod]
    public void MatchTheNamespaceNotAPrefix()
    {
        // Arrange -- the namespace, a namespace beneath it, and one that merely starts with the same letters
        const string source = """
            using Conduit.Attributes;
            using Conduit.Messages;

            [assembly: RemoteContracts("Inventory")]

            namespace Inventory
            {
                public sealed record ReserveStock(string Sku) : ICommand;
            }

            namespace Inventory.Counts
            {
                public sealed record RecountStock(string Sku) : ICommand;
            }

            namespace InventoryAudit
            {
                public sealed record RecordAdjustment(string Sku) : ICommand;
            }
            """;

        // Act
        var (_, diagnostics) = RunGenerator(source);

        // Assert
        diagnostics.Where(d => d.Id == "CONDUIT001").Select(d => d.GetMessage(CultureInfo.InvariantCulture))
            .Should().ContainSingle()
            .Which.Should().Contain("InventoryAudit.RecordAdjustment");
    }
}
