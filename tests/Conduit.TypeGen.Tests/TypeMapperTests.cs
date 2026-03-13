using AwesomeAssertions;
using Conduit.TypeGen;

namespace Conduit.TypeGen.Tests;

[TestClass]
public class TypeMapperTests
{
    [TestMethod]
    public void MapsStringToString() =>
        TypeMapper.MapType(typeof(string)).Should().Be("string");

    [TestMethod]
    public void MapsGuidToString() =>
        TypeMapper.MapType(typeof(Guid)).Should().Be("string");

    [TestMethod]
    public void MapsIntToNumber() =>
        TypeMapper.MapType(typeof(int)).Should().Be("number");

    [TestMethod]
    public void MapsLongToNumber() =>
        TypeMapper.MapType(typeof(long)).Should().Be("number");

    [TestMethod]
    public void MapsDecimalToNumber() =>
        TypeMapper.MapType(typeof(decimal)).Should().Be("number");

    [TestMethod]
    public void MapsDoubleToNumber() =>
        TypeMapper.MapType(typeof(double)).Should().Be("number");

    [TestMethod]
    public void MapsBoolToBoolean() =>
        TypeMapper.MapType(typeof(bool)).Should().Be("boolean");

    [TestMethod]
    public void MapsDateTimeToString() =>
        TypeMapper.MapType(typeof(DateTime)).Should().Be("string");

    [TestMethod]
    public void MapsDateTimeOffsetToString() =>
        TypeMapper.MapType(typeof(DateTimeOffset)).Should().Be("string");

    [TestMethod]
    public void MapsArrayToTsArray() =>
        TypeMapper.MapType(typeof(string[])).Should().Be("string[]");

    [TestMethod]
    public void MapsListToTsArray() =>
        TypeMapper.MapType(typeof(List<int>)).Should().Be("number[]");

    [TestMethod]
    public void MapsIEnumerableToTsArray() =>
        TypeMapper.MapType(typeof(IEnumerable<string>)).Should().Be("string[]");

    [TestMethod]
    public void MapsDictionaryToRecord() =>
        TypeMapper.MapType(typeof(Dictionary<string, int>)).Should().Be("Record<string, number>");

    [TestMethod]
    public void MapsNullableToUnion() =>
        TypeMapper.MapType(typeof(int?)).Should().Be("number | null");

    [TestMethod]
    public void MapsEnumToStringLiteralUnion()
    {
        var result = TypeMapper.MapType(typeof(TestEnum));
        result.Should().Contain("'A'");
        result.Should().Contain("'B'");
        result.Should().Contain("'C'");
    }

    private enum TestEnum { A, B, C }
}
