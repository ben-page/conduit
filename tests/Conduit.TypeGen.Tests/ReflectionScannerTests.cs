using AwesomeAssertions;
using Conduit.Analysis;

namespace Conduit.TypeGen.Tests;

[TestClass]
public class ReflectionScannerTests
{
    private static readonly IReadOnlyList<DispatchableTypeInfo> ScannedTypes =
        ReflectionScanner.Scan(typeof(ReflectionScannerTests).Assembly);

    [TestMethod]
    public void FindsCommandWithResult()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestCreateCommand");
        type.Should().NotBeNull();
        type.Kind.Should().Be(DispatchableKind.Command);
        type.ResultTypeName.Should().Be("Guid");
    }

    [TestMethod]
    public void FindsVoidCommand()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestDeleteCommand");
        type.Should().NotBeNull();
        type.Kind.Should().Be(DispatchableKind.VoidCommand);
    }

    [TestMethod]
    public void FindsQuery()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestGetQuery");
        type.Should().NotBeNull();
        type.Kind.Should().Be(DispatchableKind.Query);
        type.ResultTypeName.Should().Be("String");
    }

    [TestMethod]
    public void FindsEvent()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestItemCreatedEvent");
        type.Should().NotBeNull();
        type.Kind.Should().Be(DispatchableKind.Event);
    }

    [TestMethod]
    public void NotDispatchable_ExcludesType()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestExcludedCommand");
        type.Should().BeNull();
    }

    [TestMethod]
    public void Dispatchable_IncludesTypeOutsideNamespace()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestExplicitCommand");
        type.Should().NotBeNull();
    }

    [TestMethod]
    public void OrphanType_WithoutDispatchable_IsExcluded()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestOrphanCommand");
        type.Should().BeNull();
    }

    [TestMethod]
    public void DetectsValidateOverride()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestValidatedCommand");
        type.Should().NotBeNull();
        type.HasValidateOverride.Should().BeTrue();
    }

    [TestMethod]
    public void GetDeclaredNamespaces_ReadsAssemblyAttribute()
    {
        var namespaces = ReflectionScanner.GetDeclaredNamespaces(typeof(ReflectionScannerTests).Assembly);
        namespaces.Should().Contain("Conduit.TypeGen.Tests.Sample");
    }
}
