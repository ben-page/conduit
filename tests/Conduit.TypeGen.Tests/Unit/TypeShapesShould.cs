using AwesomeAssertions;
using Conduit.Analysis;
using Conduit.TypeGen.Tests.Support;

namespace Conduit.TypeGen.Tests.Unit;

// The shapes a real host's contracts take (Sample/TypeShapeSampleTypes.cs, Sample/FrameworkPrefixSampleTypes.cs), each
// asserted on the interface TypeGen emits through the real build path. The golden (EmittedGoldenShould) type-checks the
// same output with tsc; these say which line is meant to read how.
[TestClass]
public class TypeShapesShould
{
    private static readonly Lazy<string> Generated = new(TypeGenRun.GenerateForThisAssembly);

    // --- One identifier, one declaration. ---

    // Two DTOs sharing a short name would be two `export interface Item` declarations, which TypeScript merges without a
    // word -- the client then accepts either shape for both.
    [TestMethod]
    public void ThrowWhenTwoInterfacesShareAShortName()
    {
        // Arrange -- the two roots are handed in: neither is [ClientVisible], so no other test that scans this assembly
        // reaches the pair (Sample/UnreachedSampleTypes.cs).
        var assembly = typeof(TypeShapesShould).Assembly;
        List<DispatchableTypeInfo> roots = [ModelRoot(typeof(SampleDomain.Orders.OrderLines)), ModelRoot(typeof(SampleDomain.Catalog.CatalogPage))];

        // Act
        var act = () => MergeFileEmitter.Emit(roots, [assembly]);

        // Assert
        var thrown = act.Should().Throw<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("'SampleDomain.Orders.Item'");
        thrown.Which.Message.Should().Contain("'SampleDomain.Catalog.Item'");
        thrown.Which.Message.Should().Contain("export interface Item");
    }

    // A DTO named like an identifier the file always declares or imports collides with it the same way.
    [TestMethod]
    public void ThrowWhenAnInterfaceTakesAnIdentifierTheFileAlwaysDeclares()
    {
        // Arrange
        List<DispatchableTypeInfo> roots =
        [
            new() { TypeName = "DispatchResult", FullTypeName = "MyApp.Models.DispatchResult", Namespace = "MyApp.Models", Kind = DispatchableKind.Model },
        ];

        // Act
        var act = () => MergeFileEmitter.Emit(roots);

        // Assert
        var thrown = act.Should().Throw<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("'MyApp.Models.DispatchResult'");
        thrown.Which.Message.Should().Contain("DispatchResult");
    }

    // --- Generic wrappers. ---

    [TestMethod]
    public void EmitAGenericReachedAsTwoInstantiationsOnceAsItsDefinition()
    {
        // Act
        var generated = Generated.Value;

        // Assert
        Occurrences(generated, "export interface PagedResult<").Should().Be(1);
        InterfaceBlock(generated, "export interface PagedResult<T> {").Should().Be(
            Lines("export interface PagedResult<T> {", "  items: T[];", "  totalCount: number;", "}"));
        InterfaceBlock(generated, "export interface ShapeOrderBook {").Should().Contain("  orders: PagedResult<ShapeOrder>;")
            .And.Contain("  customers: PagedResult<ShapeCustomer>;");
        generated.Should().Contain("export interface ShapeOrder {", "a type argument is reached like any type");
        generated.Should().Contain("export interface ShapeCustomer {");
    }

    [TestMethod]
    public void EmitAGenericWrapperReachedOnlyAsAResult()
    {
        // Act
        var generated = Generated.Value;

        // Assert
        InterfaceBlock(generated, "export interface Envelope<T> {").Should().Be(
            Lines("export interface Envelope<T> {", "  payload: T;", "  issuedAt: string;", "}"));
        generated.Should().Contain("export interface ShapeReceipt {");
        generated.Should().Contain(
            "'SampleDomain.Shapes.GetShapeReceiptQuery': { request: GetShapeReceiptQuery; response: Envelope<ShapeReceipt> };");
    }

    // --- Arrays. ---

    [TestMethod]
    public void UnwrapAnArrayResultToItsElement()
    {
        // Act
        var generated = Generated.Value;

        // Assert
        InterfaceBlock(generated, "export interface ShapeTag {").Should().Be(Lines("export interface ShapeTag {", "  label: string;", "}"));
        generated.Should().NotContain("interface ShapeTag[]");
        generated.Should().Contain("'SampleDomain.Shapes.ListShapeTagsQuery': { request: ListShapeTagsQuery; response: ShapeTag[] };");
        generated.Should().Contain("export interface ShapeShipment {", "an array of a generic wrapper reaches the wrapper's argument");
        generated.Should().Contain(
            "'SampleDomain.Shapes.ListShapeShipmentPagesQuery': { request: ListShapeShipmentPagesQuery; response: PagedResult<ShapeShipment>[] };");
    }

    // --- BCL types. ---

    [TestMethod]
    public void MapTheRemainingIntegerTypesAndHalfToNumber()
    {
        // Act
        var scalars = InterfaceBlock(Generated.Value, "export interface ShapeScalars {");

        // Assert
        scalars.Should().Contain("  sequence: number;")
            .And.Contain("  port: number;")
            .And.Contain("  offset: number;")
            .And.Contain("  ratio: number;")
            .And.Contain("  huge: number;")
            .And.Contain("  hugeUnsigned: number;");
    }

    [TestMethod]
    public void MapCharUriAndTimeSpanToString()
    {
        // Act
        var scalars = InterfaceBlock(Generated.Value, "export interface ShapeScalars {");

        // Assert
        scalars.Should().Contain("  grade: string;")
            .And.Contain("  homepage: string | null;")
            .And.Contain("  elapsed: string;");
    }

    [TestMethod]
    public void MapAHostTypeNamedLikeABclTypeToItsOwnName() =>
        TypeMapper.MapType(typeof(SampleDomain.Lookalikes.Uri)).Should().Be("Uri");

    [TestMethod]
    public void MapSetLikeImmutableAndConcurrentCollectionsToArraysAndRecords()
    {
        // Act
        var scalars = InterfaceBlock(Generated.Value, "export interface ShapeScalars {");

        // Assert
        scalars.Should().Contain("  tags: string[];")
            .And.Contain("  owners: string[];")
            .And.Contain("  codes: number[];")
            .And.Contain("  notes: string[];")
            .And.Contain("  labels: ShapeTag[];")
            .And.Contain("  frozen: string[];")
            .And.Contain("  scores: number[];")
            .And.Contain("  keys: string[];")
            .And.Contain("  ranks: Record<string, number>;")
            .And.Contain("  cache: Record<string, string>;");
    }

    // A JSON object's keys are strings whatever the dictionary's key type, and Record<boolean, T> does not compile.
    [TestMethod]
    public void MapANonStringDictionaryKeyToAStringRecordNamingTheKeyType()
    {
        // Act
        var generated = Generated.Value;
        var scalars = InterfaceBlock(generated, "export interface ShapeScalars {");

        // Assert
        scalars.Should().Contain("  names: Record<string /* int */, string>;")
            .And.Contain("  votes: Record<string /* bool */, number>;")
            .And.Contain("  swatches: Record<string /* ShapeColor */, string>;");
        generated.Should().Contain(
            "'SampleDomain.Shapes.TallyShapeVotesQuery': { request: TallyShapeVotesQuery; response: Record<string /* bool */, ShapeVote> };",
            "the type map's result path names the key the same way");
        generated.Should().Contain("export interface ShapeVote {", "a dictionary result reaches its value type");
    }

    // --- System.Text.Json marks. ---

    [TestMethod]
    public void EmitAJsonPropertyNameAsTheKey()
    {
        // Act
        var marks = InterfaceBlock(Generated.Value, "export interface ShapeJsonMarks {");

        // Assert
        marks.Should().Contain("  order_no: string;")
            .And.Contain("  'x-trace': string;", "a name that is not an identifier is quoted")
            .And.NotContain("orderNumber")
            .And.NotContain("  trace:");
    }

    [TestMethod]
    public void OmitAJsonIgnoredPropertyAndMakeAConditionallyIgnoredOneOptional()
    {
        // Act
        var marks = InterfaceBlock(Generated.Value, "export interface ShapeJsonMarks {");

        // Assert
        marks.Should().NotContain("secret")
            .And.Contain("  note?: string | null;", "WhenWritingNull leaves the key out when it is null")
            .And.Contain("  revision: number;", "Never ignores nothing");
    }

    [TestMethod]
    public void EmitAJsonStringEnumMemberNameAsTheLiteral() =>
        InterfaceBlock(Generated.Value, "export interface ShapeJsonMarks {").Should().Contain("  status: 'in-progress' | 'Done';");

    // System.Text.Json writes a combination of flags as "Read, Write", which no union of member names covers.
    [TestMethod]
    public void EmitAFlagsEnumAsString() =>
        InterfaceBlock(Generated.Value, "export interface ShapeJsonMarks {").Should().Contain("  permissions: string;");

    // --- Framework namespaces and assemblies, bounded by a dot. ---

    [TestMethod]
    public void DiscoverDtosUnderNamespacesThatOnlyLookLikeAFramework()
    {
        // Act
        var generated = Generated.Value;

        // Assert
        InterfaceBlock(generated, "export interface ShapeInvoice {").Should().Contain("  amount: number;");
        InterfaceBlock(generated, "export interface ShapeTruck {").Should().Contain("  plate: string;");
        InterfaceBlock(generated, "export interface ShapeShelf {").Should().Contain("  aisle: string;");
    }

    [TestMethod]
    [DataRow("System", true)]
    [DataRow("System.Collections.Generic", true)]
    [DataRow("Microsoft.Extensions.Logging", true)]
    [DataRow("Conduit", true)]
    [DataRow("Conduit.Messages", true)]
    [DataRow("Conduit.Runtime", true)]
    [DataRow("Conduit.Configuration", true)]
    [DataRow("Conduit.Middleware", true)]
    [DataRow("Conduit.Handlers", true)]
    [DataRow("Conduit.Attributes", true)]
    [DataRow("Conduit.Exceptions", true)]
    [DataRow("Conduit.Runtime.Internal", true)]
    [DataRow("SystemsInc.Fleet", false)]
    [DataRow("MicrosoftFans", false)]
    [DataRow("ConduitApp.Billing", false)]
    [DataRow("Conduit.Sample.Notes", false)]
    [DataRow("Conduit.MessagesExtra", false)]
    [DataRow("", false)]
    public void TreatOnlySystemMicrosoftAndConduitsOwnNamespacesAsFramework(string ns, bool expected) =>
        TypeCollector.IsFrameworkNamespace(ns).Should().Be(expected);

    [TestMethod]
    [DataRow("System", true)]
    [DataRow("System.Runtime", true)]
    [DataRow("Microsoft.Extensions.Logging.Abstractions", true)]
    [DataRow("netstandard", true)]
    [DataRow("mscorlib", true)]
    [DataRow("WindowsBase", true)]
    [DataRow("SystemsInc.Contracts", false)]
    [DataRow("MicrosoftFans.Contracts", false)]
    [DataRow("Conduit.Sample.Notes", false)]
    public void TreatOnlyDotBoundedSystemAndMicrosoftAssembliesAsFramework(string assemblyName, bool expected) =>
        TypeGenTask.IsFrameworkAssembly(assemblyName).Should().Be(expected);

    private static DispatchableTypeInfo ModelRoot(Type type) => new()
    {
        TypeName = type.Name,
        FullTypeName = type.FullName!,
        Namespace = type.Namespace!,
        Kind = DispatchableKind.Model,
    };

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static int Occurrences(string text, string value) => text.Split(value).Length - 1;

    /// <summary>The interface that starts with <paramref name="header"/>, through its closing brace, with LF line ends.</summary>
    private static string InterfaceBlock(string generated, string header)
    {
        var text = generated.ReplaceLineEndings("\n");
        var start = text.IndexOf(header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the output declares '{header}'");
        var end = text.IndexOf("\n}", start, StringComparison.Ordinal);
        return text[start..(end + 2)];
    }
}
