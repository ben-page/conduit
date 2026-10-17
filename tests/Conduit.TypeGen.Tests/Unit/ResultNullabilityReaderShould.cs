using System.Reflection;
using System.Runtime.InteropServices;
using AwesomeAssertions;

namespace Conduit.TypeGen.Tests.Unit;

[TestClass]
public sealed class ResultNullabilityReaderShould
{
    private const string QueryInterfaceName = "Conduit.Messages.IQuery`1";
    private const string CommandWithResultInterfaceName = "Conduit.Messages.ICommand`1";
    private const string SampleNamespace = "Conduit.TypeGen.Tests.Sample";

    private static MetadataLoadContext metadataContext = null!;
    private static Assembly sampleAssembly = null!;

    [ClassInitialize]
    public static void LoadSampleAssembly(TestContext context)
    {
        // The real build path: TypeGenTask loads the compiled assembly into a MetadataLoadContext, where nothing runs
        // and every Type is a metadata view. Resolve from the test output first, then the runtime -- deduped by simple
        // name, as TypeGenTask does, because a MetadataLoadContext refuses two files for one assembly.
        var assemblyPath = typeof(ResultNullabilityReaderShould).Assembly.Location;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = Directory.GetFiles(Path.GetDirectoryName(assemblyPath)!, "*.dll")
            .Concat(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
            .Where(path => seen.Add(Path.GetFileNameWithoutExtension(path)))
            .ToList();

        metadataContext = new MetadataLoadContext(new PathAssemblyResolver(paths));
        sampleAssembly = metadataContext.LoadFromAssemblyPath(assemblyPath);
    }

    [ClassCleanup]
    public static void DisposeMetadataContext() => metadataContext.Dispose();

    [TestMethod]
    public void Reads_a_nullable_result() =>
        IsResultNullable("TestFindQuery", QueryInterfaceName).Should().BeTrue();

    [TestMethod]
    public void Reads_a_not_null_result() =>
        IsResultNullable("TestRequireQuery", QueryInterfaceName).Should().BeFalse();

    [TestMethod]
    public void Leaves_a_value_type_result_to_the_type_map() =>
        IsResultNullable("TestCountOrNoneQuery", QueryInterfaceName).Should().BeFalse(
            "Nullable<int> already maps to `number | null`");

    [TestMethod]
    public void Reads_a_nullable_generic_result() =>
        IsResultNullable("TestFindManyQuery", QueryInterfaceName).Should().BeTrue();

    [TestMethod]
    public void Reads_a_command_result() =>
        IsResultNullable("TestRenameCommand", CommandWithResultInterfaceName).Should().BeTrue();

    [TestMethod]
    public void Reads_a_result_declared_on_a_base_record() =>
        IsResultNullable("TestDerivedFindQuery", QueryInterfaceName).Should().BeTrue();

    [TestMethod]
    public void Reads_a_nested_query_through_its_containing_type() =>
        IsResultNullable("TestOuter+TestNestedFindQuery", QueryInterfaceName).Should().BeTrue();

    [TestMethod]
    public void An_explicit_not_null_result_wins_over_a_nullable_context()
    {
        // Arrange -- the precondition this test depends on: the compiler gave the type a nullable context.
        var messageType = SampleType("TestMostlyNullableQuery");
        ResultNullabilityReader.NullableContextOf(messageType).Should().Be(2);

        // Act
        using var reader = new ResultNullabilityReader();
        var nullable = reader.IsResultNullable(messageType, QueryInterfaceName);

        // Assert
        nullable.Should().BeFalse();
    }

    [TestMethod]
    public void Treats_an_oblivious_result_as_not_nullable() =>
        IsResultNullable("TestObliviousQuery", QueryInterfaceName).Should().BeFalse();

    private static bool IsResultNullable(string sampleName, string genericInterfaceFullName)
    {
        // Arrange
        var messageType = SampleType(sampleName);
        using var reader = new ResultNullabilityReader();

        // Act
        return reader.IsResultNullable(messageType, genericInterfaceFullName);
    }

    private static Type SampleType(string sampleName) =>
        sampleAssembly.GetType($"{SampleNamespace}.{sampleName}", throwOnError: true)!;
}
