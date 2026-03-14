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

    private enum TestEnum { A, B, C }
}
