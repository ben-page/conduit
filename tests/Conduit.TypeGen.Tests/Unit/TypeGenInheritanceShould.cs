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

    [TestMethod]
    public void EmitMultiLevelInheritanceChain()
    {
        var assembly = typeof(TypeGenInheritanceShould).Assembly;
        var types = ReflectionScanner.ScanClientVisible(assembly);

        var result = MergeFileEmitter.Emit(types, [assembly], includeSignalR: false);

        result.Should().Contain("export interface LevelA {");
        result.Should().Contain("export interface LevelB extends LevelA {");
        result.Should().Contain("export interface LevelC extends LevelB {");
        result.Should().Contain("export interface LevelD extends LevelB {");
    }

    [TestMethod]
    public void ExcludeLocalOnlyDerivedTypes()
    {
        var assembly = typeof(TypeGenInheritanceShould).Assembly;
        var types = ReflectionScanner.ScanClientVisible(assembly);

        var result = MergeFileEmitter.Emit(types, [assembly], includeSignalR: false);

        result.Should().Contain("export interface PublicAuditEvent extends AuditEventBase {");
        result.Should().Contain("  userId: string;");

        result.Should().NotContain("InternalAuditEvent");
        result.Should().NotContain("secretToken");
    }

    [TestMethod]
    public void EmitBaseArrayPropertyAndAllSubtypes()
    {
        var assembly = typeof(TypeGenInheritanceShould).Assembly;
        var types = ReflectionScanner.ScanClientVisible(assembly);

        var result = MergeFileEmitter.Emit(types, [assembly], includeSignalR: false);

        result.Should().Contain("export interface WidgetContainer {");
        result.Should().Contain("  widgets: WidgetBase[];");

        result.Should().Contain("export interface WidgetBase {");
        result.Should().Contain("export interface TextWidget extends WidgetBase {");
        result.Should().Contain("  text: string;");
        result.Should().Contain("export interface IconWidget extends WidgetBase {");
        result.Should().Contain("  icon: string;");
    }

    [TestMethod]
    public void FlatEmitSubtypeWhenBaseNotClientVisible()
    {
        var assembly = typeof(TypeGenInheritanceShould).Assembly;
        var types = ReflectionScanner.ScanClientVisible(assembly);

        var result = MergeFileEmitter.Emit(types, [assembly], includeSignalR: false);

        result.Should().Contain("export interface AnnotatedDerived {");
        result.Should().NotContain("AnnotatedDerived extends");
        result.Should().Contain("  inheritedField: string;");
        result.Should().Contain("  ownField: number;");

        result.Should().NotContain("export interface UnannotatedBase");
    }

    [TestMethod]
    public void LinkConcreteClientVisibleSubtypesToAbstractBase()
    {
        // Arrange — subtypes carry [ClientVisible] themselves AND inherit from
        // an abstract [ClientVisible] base. They should still emit with `extends`,
        // not flat with duplicated base fields.
        var assembly = typeof(TypeGenInheritanceShould).Assembly;
        var types = ReflectionScanner.ScanClientVisible(assembly);

        // Act
        var result = MergeFileEmitter.Emit(types, [assembly], includeSignalR: false);

        // Assert — both subtypes extend NotifyBase
        result.Should().Contain("export interface EmailNotify extends NotifyBase {");
        result.Should().Contain("  toAddress: string;");
        result.Should().Contain("export interface SmsNotify extends NotifyBase {");
        result.Should().Contain("  phoneNumber: string;");

        // Assert — inherited field (MessageId) does NOT appear in subtypes
        var emailStart = result.IndexOf("export interface EmailNotify extends", StringComparison.Ordinal);
        var emailEnd = result.IndexOf('}', emailStart);
        var emailBlock = result[emailStart..emailEnd];
        emailBlock.Should().NotContain("messageId");
        emailBlock.Should().Contain("toAddress");
    }
}
