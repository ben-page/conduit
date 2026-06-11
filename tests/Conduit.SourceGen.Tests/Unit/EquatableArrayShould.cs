using AwesomeAssertions;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class EquatableArrayShould
{
    [TestMethod]
    public void BeEqualForSameElementsInSameOrder()
    {
        var a = new EquatableArray<string>(["x", "y"]);
        var b = new EquatableArray<string>(["x", "y"]);
        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [TestMethod]
    public void NotBeEqualForDifferentOrder()
    {
        var a = new EquatableArray<string>(["x", "y"]);
        var b = new EquatableArray<string>(["y", "x"]);
        a.Equals(b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [TestMethod]
    public void NotBeEqualForDifferentLength()
    {
        var a = new EquatableArray<string>(["x"]);
        var b = new EquatableArray<string>(["x", "y"]);
        a.Equals(b).Should().BeFalse();
    }

    [TestMethod]
    public void TreatDefaultAndEmptyAsEqual()
    {
        var def = default(EquatableArray<string>);
        var empty = new EquatableArray<string>([]);
        def.Equals(empty).Should().BeTrue();
        def.GetHashCode().Should().Be(empty.GetHashCode());
        def.Count.Should().Be(0);
        empty.Count.Should().Be(0);
    }

    [TestMethod]
    public void ExposeCountAndIndexerAndEnumeration()
    {
        var a = new EquatableArray<string>(["x", "y", "z"]);
        a.Count.Should().Be(3);
        a[1].Should().Be("y");
        a.ToList().Should().Equal("x", "y", "z");
    }

    [TestMethod]
    public void BuildFromEnumerableViaToEquatableArray()
    {
        IEnumerable<int> source = [1, 2, 3];
        var a = source.ToEquatableArray();
        a.Count.Should().Be(3);
        a.Should().Equal(1, 2, 3);
    }
}
