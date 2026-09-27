using AwesomeAssertions;
using Conduit.Exceptions;

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

    /// <summary>Pins the hardcoded assembly name <c>ErrorTypeScanner</c> re-implements
    /// <c>ConduitException.IsClientTyped</c>'s "outside Conduit.Contracts" rule by (it cannot compare
    /// <see cref="Type.Assembly"/> identity under <c>MetadataLoadContext</c>) to the real Conduit.Contracts
    /// assembly name, so a rename of that project cannot drift silently past the re-implementation.</summary>
    [TestMethod]
    public void HardcodeTheRealConduitContractsAssemblyName() =>
        ErrorTypeScanner.ConduitContractsAssemblyName.Should().Be(typeof(ConduitException).Assembly.GetName().Name);
}
