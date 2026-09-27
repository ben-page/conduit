using AwesomeAssertions;

namespace Conduit.TypeGen.Tests.Unit;

[TestClass]
public class ErrorTypeScannerShould
{
    private static readonly IReadOnlyList<ErrorTypeInfo> Found =
        ErrorTypeScanner.Scan([typeof(ErrorTypeScannerShould).Assembly]);

    [TestMethod]
    public void FindASubclassThatDeclaresData() =>
        Found.Should().ContainSingle(e => e.FullTypeName == "Conduit.TypeGen.Tests.Sample.SampleBlockedException")
            .Which.Properties.Select(p => p.Name).Should().BeEquivalentTo(["Total", "Names"]);

    [TestMethod]
    public void FindAConsumerSubclassThatDeclaresNothing_WithNoProperties() =>
        Found.Should().ContainSingle(e => e.FullTypeName == "Conduit.TypeGen.Tests.Sample.SamplePlainException")
            .Which.Properties.Should().BeEmpty();

    [TestMethod]
    public void SkipTheBuiltInStatusExceptions() =>
        Found.Should().NotContain(e => e.FullTypeName.StartsWith("Conduit.Exceptions.", StringComparison.Ordinal));
}
