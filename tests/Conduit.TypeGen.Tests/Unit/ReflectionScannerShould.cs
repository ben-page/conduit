using AwesomeAssertions;
using Conduit.Analysis;

namespace Conduit.TypeGen.Tests.Unit;

[TestClass]
public class ReflectionScannerShould
{
    private static readonly IReadOnlyList<DispatchableTypeInfo> ScannedTypes =
        ReflectionScanner.Scan(typeof(ReflectionScannerShould).Assembly);

    [TestMethod]
    public void FindCommandWithResult()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestCreateCommand");
        type.Should().NotBeNull();
        type.Kind.Should().Be(DispatchableKind.Command);
        type.ResultTypeName.Should().Be("Guid");
    }

    [TestMethod]
    public void FindVoidCommand()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestDeleteCommand");
        type.Should().NotBeNull();
        type.Kind.Should().Be(DispatchableKind.VoidCommand);
    }

    [TestMethod]
    public void FindQuery()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestGetQuery");
        type.Should().NotBeNull();
        type.Kind.Should().Be(DispatchableKind.Query);
        type.ResultTypeName.Should().Be("String");
    }

    [TestMethod]
    public void FindEvent()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestItemCreatedEvent");
        type.Should().NotBeNull();
        type.Kind.Should().Be(DispatchableKind.Event);
    }

    [TestMethod]
    public void ExcludeNotDispatchableType()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestExcludedCommand");
        type.Should().BeNull();
    }

    [TestMethod]
    public void IncludeDispatchableTypeOutsideNamespace()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestExplicitCommand");
        type.Should().NotBeNull();
    }

    [TestMethod]
    public void ExcludeOrphanTypeWithoutDispatchable()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestOrphanCommand");
        type.Should().BeNull();
    }

    [TestMethod]
    public void DetectValidateOverride()
    {
        var type = ScannedTypes.FirstOrDefault(t => t.TypeName == "TestValidatedCommand");
        type.Should().NotBeNull();
        type.HasValidateOverride.Should().BeTrue();
    }

    [TestMethod]
    public void GetDeclaredNamespacesFromAssemblyAttribute()
    {
        var namespaces = ReflectionScanner.GetDeclaredNamespaces(typeof(ReflectionScannerShould).Assembly);
        namespaces.Should().Contain("Conduit.TypeGen.Tests.Sample");
    }
}
