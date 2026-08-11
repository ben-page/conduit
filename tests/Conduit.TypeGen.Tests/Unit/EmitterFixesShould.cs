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

    [TestMethod]
    public void EmitGenericInterfaceDeclarationWithTypeParameter()
    {
        var result = EmitFromTestAssembly();

        // The generic interface declaration itself must strip the arity backtick and emit the
        // type parameter (`PagedResult`1` → `PagedResult<T>`), never the raw CLR name.
        result.Should().Contain("export interface TestPagedResult<T> {");
        result.Should().NotContain("TestPagedResult`1");
    }

    [TestMethod]
    public void EmitGenericQueryResultInMapWithAngleBrackets()
    {
        var result = EmitFromTestAssembly();

        // A query returning PagedResult<Item> goes through TypeMapEmitter.ResolveResultType, whose
        // C#-style angle-bracket result name can't be reflection-resolved and must instead be
        // reconstructed by MapFullTypeName as `PagedResult<Item>`, never the raw backtick name.
        result.Should().Contain("response: TestPagedResult<TestActivityListItem>");
        result.Should().NotContain("PagedResult`1");
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

    // --- Fix 4: IReadOnlyList<TEnum> query result must inline the enum as a string-literal union. ---

    [TestMethod]
    public void MapEnumListTypeToParenthesizedStringLiteralUnionArray()
    {
        // Reflection path (a resolved Type is available) already worked before this fix --
        // TypeMapper.MapType's generic-collection branch recurses into the enum branch correctly.
        // This pins that it stays correct, so Fix 4 is provably about the OTHER path below.
        var result = TypeMapper.MapType(typeof(IReadOnlyList<TestActivity>));
        result.Should().Be("('Run' | 'Idle' | 'TearDown')[]");
    }

    [TestMethod]
    public void EmitEnumListQueryResultAsStringLiteralUnionArray_NotBareEnumName()
    {
        var result = EmitFromTestAssembly();

        // TypeMapEmitter.ResolveResultType's reflection resolution can't parse
        // GetResultFullTypeName's C#-style "IReadOnlyList<TestActivity>" text back into a real
        // Type (Assembly.GetType/Type.GetType both expect CLR backtick-and-brackets generic
        // syntax, not C# angle brackets) -- exactly like Fix 1's PagedResult<Item> case above --
        // so this falls through to TypeMapper.MapFullTypeName's string-based path. Before this
        // fix, that path's collection branch had no way to recognize TestActivity as an enum (it
        // is not in SimpleTypeMappings, and the reflection retry used Type.GetType, which cannot
        // see MetadataLoadContext-loaded types) and fell back to the bare short name "TestActivity[]"
        // -- a TypeScript compile error, since no `TestActivity` type or import exists on the client.
        result.Should().Contain("response: ('Run' | 'Idle' | 'TearDown')[]");
        result.Should().NotContain("response: TestActivity[]");
    }
}
