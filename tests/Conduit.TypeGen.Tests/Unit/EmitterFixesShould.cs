using System.Text.Json;
using AwesomeAssertions;
using Conduit.TypeGen.Tests.Sample;

namespace Conduit.TypeGen.Tests.Unit;

[TestClass]
public class EmitterFixesShould
{
    private static string EmitFromTestAssembly()
    {
        var assembly = typeof(EmitterFixesShould).Assembly;
        var types = ReflectionScanner.ScanClientVisible(assembly);
        return MergeFileEmitter.Emit(types, [assembly], includeSignalR: false);
    }

    // --- Fix 1: generic result types ---

    [TestMethod]
    public void MapGenericTypeToAngleBracketsStrippingArityBacktick()
    {
        var result = TypeMapper.MapType(typeof(TestPagedResult<TestActivityListItem>));
        result.Should().Be("TestPagedResult<TestActivityListItem>");
        result.Should().NotContain("`");
    }

    [TestMethod]
    public void EmitGenericPropertyWithAngleBrackets()
    {
        var result = EmitFromTestAssembly();

        // A property typed as PagedResult<Item> reflects over a resolved closed generic and is
        // routed through TypeMapper.MapType, which must emit angle brackets, never a backtick name.
        result.Should().Contain("  results: TestPagedResult<TestActivityListItem>;");
    }

    // --- Fix 2: JsonElement → unknown ---

    [TestMethod]
    public void MapJsonElementToUnknown() =>
        TypeMapper.MapType(typeof(JsonElement)).Should().Be("unknown");

    [TestMethod]
    public void EmitJsonElementPropertyAsUnknown()
    {
        var result = EmitFromTestAssembly();

        result.Should().Contain("  payload: unknown;");
        result.Should().NotContain("JsonElement");
    }

    // --- Fix 3: nullable reference types ---

    [TestMethod]
    public void EmitNullableReferencePropertyAsUnion()
    {
        var result = EmitFromTestAssembly();

        result.Should().Contain("  optional: string | null;");
        result.Should().Contain("  required: string;");
    }
}
