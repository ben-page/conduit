using AwesomeAssertions;

namespace Conduit.TypeGen.Tests.Unit;

[TestClass]
public class TypeGenInheritanceShould
{
    [TestMethod]
    public void EmitAbstractBaseAndDerivedSubtypes()
    {
        // Arrange
        var assembly = typeof(TypeGenInheritanceShould).Assembly;
        var types = ReflectionScanner.ScanClientVisible(assembly);

        // Act
        var result = MergeFileEmitter.Emit(types, [assembly], includeSignalR: false);

        // Assert — base interface emitted with own fields
        result.Should().Contain("export interface PidControlBase {");
        result.Should().Contain("  tagName: string;");
        result.Should().Contain("  borderColor: string;");

        // Assert — subtypes extend base with their own fields only
        result.Should().Contain("export interface PidValueControl extends PidControlBase {");
        result.Should().Contain("  alarmTag: string;");
        result.Should().Contain("  format: string;");

        result.Should().Contain("export interface PidBooleanControl extends PidControlBase {");
        result.Should().Contain("  onColor: string;");
        result.Should().Contain("  offColor: string;");
    }

    [TestMethod]
    public void NotDuplicateInheritedPropertiesInSubtypes()
    {
        // Arrange
        var assembly = typeof(TypeGenInheritanceShould).Assembly;
        var types = ReflectionScanner.ScanClientVisible(assembly);

        // Act
        var result = MergeFileEmitter.Emit(types, [assembly], includeSignalR: false);

        // Assert — slice each subtype's block and verify base fields are not present
        var valueStart = result.IndexOf("export interface PidValueControl extends", StringComparison.Ordinal);
        var valueEnd = result.IndexOf('}', valueStart);
        var valueBlock = result[valueStart..valueEnd];

        valueBlock.Should().NotContain("tagName");
        valueBlock.Should().NotContain("borderColor");
        valueBlock.Should().Contain("alarmTag");
    }
}
