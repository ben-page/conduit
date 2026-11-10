using AwesomeAssertions;

namespace Conduit.TypeGen.Tests.Unit;

[TestClass]
public class TypeMapperShould
{
    [TestMethod]
    public void MapStringToString() =>
        TypeMapper.MapType(typeof(string)).Should().Be("string");

    [TestMethod]
    public void MapGuidToString() =>
        TypeMapper.MapType(typeof(Guid)).Should().Be("string");

    [TestMethod]
    public void MapIntToNumber() =>
        TypeMapper.MapType(typeof(int)).Should().Be("number");

    [TestMethod]
    public void MapLongToNumber() =>
        TypeMapper.MapType(typeof(long)).Should().Be("number");

    [TestMethod]
    public void MapUIntToNumber() =>
        TypeMapper.MapType(typeof(uint)).Should().Be("number");

    [TestMethod]
    public void MapDecimalToNumber() =>
        TypeMapper.MapType(typeof(decimal)).Should().Be("number");

    [TestMethod]
    public void MapDoubleToNumber() =>
        TypeMapper.MapType(typeof(double)).Should().Be("number");

    [TestMethod]
    public void MapBoolToBoolean() =>
        TypeMapper.MapType(typeof(bool)).Should().Be("boolean");

    [TestMethod]
    public void MapDateTimeToString() =>
        TypeMapper.MapType(typeof(DateTime)).Should().Be("string");

    [TestMethod]
    public void MapDateTimeOffsetToString() =>
        TypeMapper.MapType(typeof(DateTimeOffset)).Should().Be("string");

    [TestMethod]
    public void MapArrayToTsArray() =>
        TypeMapper.MapType(typeof(string[])).Should().Be("string[]");

    // System.Text.Json writes byte[] as a base64 string, not a JSON array.
    [TestMethod]
    public void MapByteArrayToString() =>
        TypeMapper.MapType(typeof(byte[])).Should().Be("string");

    [TestMethod]
    public void MapArrayOfByteArraysToStringArray() =>
        TypeMapper.MapType(typeof(byte[][])).Should().Be("string[]");

    // Only byte[] gets the base64 converter; a List<byte> is still a JSON array of numbers.
    [TestMethod]
    public void MapListOfByteToNumberArray() =>
        TypeMapper.MapType(typeof(List<byte>)).Should().Be("number[]");

    [TestMethod]
    public void MapListToTsArray() =>
        TypeMapper.MapType(typeof(List<int>)).Should().Be("number[]");

    [TestMethod]
    public void MapIEnumerableToTsArray() =>
        TypeMapper.MapType(typeof(IEnumerable<string>)).Should().Be("string[]");

    [TestMethod]
    public void MapDictionaryToRecord() =>
        TypeMapper.MapType(typeof(Dictionary<string, int>)).Should().Be("Record<string, number>");

    [TestMethod]
    public void MapNullableToUnion() =>
        TypeMapper.MapType(typeof(int?)).Should().Be("number | null");

    // The STRING path's twin of MapNullableToUnion above. It lacked this case, so a handler declaring
    // IQuery<int?> emitted "Nullable<number>" -- undeclared in the generated file, so the client did not
    // compile; the only workaround was to wrap the response in a record.
    [TestMethod]
    public void MapFullTypeNameNullableToUnion() =>
        TypeMapper.MapFullTypeName("System.Nullable<System.Int32>").Should().Be("number | null");

    [TestMethod]
    public void MapFullTypeNameNullableCollectionElement() =>
        TypeMapper.MapFullTypeName("System.Collections.Generic.List<System.Nullable<System.Int32>>")
            .Should().Be("(number | null)[]");

    [TestMethod]
    public void MapEnumToStringLiteralUnion()
    {
        var result = TypeMapper.MapType(typeof(TestEnum));
        result.Should().Contain("'A'");
        result.Should().Contain("'B'");
        result.Should().Contain("'C'");
    }

    [TestMethod]
    public void MapArrayOfAtomicTypeWithoutParens() =>
        TypeMapper.MapType(typeof(string[])).Should().Be("string[]");

    [TestMethod]
    public void MapCollectionOfAtomicTypeWithoutParens() =>
        TypeMapper.MapType(typeof(IReadOnlyCollection<int>)).Should().Be("number[]");

    [TestMethod]
    public void MapArrayOfEnumParenthesizesUnion()
    {
        // Bug: was emitting  'A' | 'B' | 'C'[]  which binds [] only to 'C'
        // Should emit  ('A' | 'B' | 'C')[]
        var result = TypeMapper.MapType(typeof(TestEnum[]));
        result.Should().Be("('A' | 'B' | 'C')[]");
    }

    [TestMethod]
    public void MapCollectionOfEnumParenthesizesUnion()
    {
        // Bug: was emitting  'A' | 'B' | 'C'[]  which binds [] only to 'C'
        // Should emit  ('A' | 'B' | 'C')[]
        var result = TypeMapper.MapType(typeof(IReadOnlyCollection<TestEnum>));
        result.Should().Be("('A' | 'B' | 'C')[]");
    }

    [TestMethod]
    public void MapListOfEnumParenthesizesUnion()
    {
        var result = TypeMapper.MapType(typeof(List<TestEnum>));
        result.Should().Be("('A' | 'B' | 'C')[]");
    }

    [TestMethod]
    public void EndToEnd_CollectionOfEnumPropertyEmitsParenthesizedUnionArray()
    {
        // Arrange
        var assembly = typeof(TypeMapperShould).Assembly;
        var types = ReflectionScanner.ScanClientVisible(assembly);

        // Act
        var result = MergeFileEmitter.Emit(types, [assembly], includeSignalR: false);

        // Assert — permissions: ('Read' | 'Write' | 'Admin')[]  NOT  'Read' | 'Write' | 'Admin'[]
        result.Should().Contain("  permissions: ('Read' | 'Write' | 'Admin')[];");
    }

    private enum TestEnum { A, B, C }
}
