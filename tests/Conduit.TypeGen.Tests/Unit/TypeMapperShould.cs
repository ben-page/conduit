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
