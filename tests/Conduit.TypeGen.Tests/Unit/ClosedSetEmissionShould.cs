using AwesomeAssertions;
using Conduit.Attributes;
using Conduit.Messages;
using Conduit.TypeGen.Tests.Sample;
using Conduit.TypeGen.Tests.Support;

namespace Conduit.TypeGen.Tests.Unit;

[TestClass]
public class ClosedSetEmissionShould
{
    private const string Union = @"'self' | 'reports' | 'o\'clock'";

    [TestMethod]
    public void MapAClosedSetToAUnionOfItsDeclaredValues() =>
        TypeMapper.MapType(typeof(TestReach)).Should().Be(Union);

    [TestMethod]
    public void ParenthesizeTheUnionInACollection() =>
        TypeMapper.MapType(typeof(IReadOnlyList<TestReach>)).Should().Be($"({Union})[]");

    [TestMethod]
    public void EmitTheDeclaredValuesUnderMetadataLoadContext()
    {
        // Arrange -- the real build path: TypeGenTask loads the compiled assembly into a MetadataLoadContext, where a
        // static initializer cannot run and a typeof comparison is always false.
        var outputDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var task = new TypeGenTask
        {
            AssemblyPath = typeof(ClosedSetEmissionShould).Assembly.Location,
            OutputDirectory = outputDir,
            Transports = "Http",
            BuildEngine = new FakeBuildEngine(),
        };

        try
        {
            // Act
            var succeeded = task.Execute();
            var generated = File.ReadAllText(Path.Combine(outputDir, "conduit.generated.ts"));

            // Assert
            succeeded.Should().BeTrue();
            generated.Should().Contain($"  reach: {Union};");
            generated.Should().Contain($"  widest: {Union} | null;");
            generated.Should().Contain($"  allowed: ({Union})[];");
            generated.Should().Contain(
                $"'Conduit.TypeGen.Tests.Sample.TestListReachesQuery': {{ request: TestListReachesQuery; response: ({Union})[] }};");
            generated.Should().Contain(
                $"'Conduit.TypeGen.Tests.Sample.TestWidestReachQuery': {{ request: TestWidestReachQuery; response: {Union} }};");
            generated.Should().NotContain("interface TestReach", "a closed set is a union, never an interface");
            generated.Should().NotContain("'Self' | 'Reports'", "the wire spelling is the value, never the member name");
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    [TestMethod]
    public void LeaveAnUnmarkedRecordAnInterface()
    {
        // Act + Assert -- control: an ordinary DTO is untouched.
        ClosedSetReader.ValuesOf(typeof(TestActivityListItem)).Should().BeNull();
        TypeMapper.MapType(typeof(TestActivityListItem)).Should().Be("TestActivityListItem");
    }

    [TestMethod]
    public void RefuseTheAttributeOnATypeThatIsNotAClosedSet()
    {
        // Act
        var act = () => ClosedSetReader.ValuesOf(typeof(ListedButNotAClosedSet));

        // Assert -- the wire would carry it as an object, so a union would be false.
        act.Should().Throw<InvalidOperationException>().WithMessage("*does not implement IClosedSet<ListedButNotAClosedSet>*");
    }

    [TestMethod]
    public void RefuseAClosedSetThatListsNoValues()
    {
        // Act
        var act = () => ClosedSetReader.ValuesOf(typeof(ListsNothing));

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*no values*");
    }

    [TestMethod]
    public void EscapeAQuoteABackslashAndALineBreak() =>
        ClosedSetReader.ToTsStringLiteral("it's a\\b\n").Should().Be(@"'it\'s a\\b\n'");

    // Private, so no whole-assembly scan (exported types only) and no [ClientVisible] graph ever reaches them: a
    // permanent bad fixture there would fail every other test that scans this assembly.
    [ClosedSet("x")]
    private sealed record ListedButNotAClosedSet(string Value);

    [ClosedSet]
    private sealed record ListsNothing : IClosedSet<ListsNothing>
    {
        public static IReadOnlyList<ListsNothing> All { get; } = [];

        public string Value { get; } = string.Empty;
    }
}
