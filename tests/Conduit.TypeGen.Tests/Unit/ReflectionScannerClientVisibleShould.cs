using AwesomeAssertions;
using Conduit.Analysis;

namespace Conduit.TypeGen.Tests.Unit;

[TestClass]
public class ReflectionScannerClientVisibleShould
{
    [TestMethod]
    public void IncludeNonDispatchableClientVisibleTypes()
    {
        // Arrange / Act
        var types = ReflectionScanner.ScanClientVisible(typeof(ReflectionScannerClientVisibleShould).Assembly);

        // Assert
        var dto = types.FirstOrDefault(t => t.TypeName == "TestItemDto");
        dto.Should().NotBeNull();
        dto.Kind.Should().Be(DispatchableKind.Model);
    }

    [TestMethod]
    public void NotIncludeNonClientVisibleNonDispatchableTypes()
    {
        // Arrange / Act
        var types = ReflectionScanner.ScanClientVisible(typeof(ReflectionScannerClientVisibleShould).Assembly);

        // Assert
        var allTypeNames = types.Select(t => t.TypeName).ToList();
        allTypeNames.Should().NotContain("ReflectionScannerClientVisibleShould");
    }

    [TestMethod]
    public void ScanClientVisible_sets_ResultIsNullable()
    {
        // Arrange / Act
        var types = ReflectionScanner.ScanClientVisible(typeof(ReflectionScannerClientVisibleShould).Assembly);

        // Assert
        types.Single(t => t.TypeName == "TestFindQuery").ResultIsNullable.Should().BeTrue();
        types.Single(t => t.TypeName == "TestRequireQuery").ResultIsNullable.Should().BeFalse();
    }
}
