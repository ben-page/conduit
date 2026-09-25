using AwesomeAssertions;
using Conduit.Analysis;

namespace Conduit.TypeGen.Tests.Unit;

[TestClass]
public class MergeFileEmitterShould
{
    private static readonly List<DispatchableTypeInfo> SampleTypes =
    [
        new()
        {
            TypeName = "CreateCommand",
            FullTypeName = "MyApp.Commands.CreateCommand",
            Namespace = "MyApp.Commands",
            Kind = DispatchableKind.Command,
            ResultTypeName = "Guid",
            ResultFullTypeName = "System.Guid",
        },
        new()
        {
            TypeName = "GetItemsQuery",
            FullTypeName = "MyApp.Queries.GetItemsQuery",
            Namespace = "MyApp.Queries",
            Kind = DispatchableKind.Query,
            ResultTypeName = "String",
            ResultFullTypeName = "System.String",
        },
        new()
        {
            TypeName = "ItemCreatedEvent",
            FullTypeName = "MyApp.Events.ItemCreatedEvent",
            Namespace = "MyApp.Events",
            Kind = DispatchableKind.Event,
        },
    ];

    [TestMethod]
    public void EmitConduitClientClass()
    {
        // Act
        var result = MergeFileEmitter.Emit(SampleTypes);

        // Assert
        result.Should().Contain("export class ConduitClient");
        result.Should().NotContain("export class Mediator");
    }

    [TestMethod]
    public void EmitExecuteMethod()
    {
        // Act
        var result = MergeFileEmitter.Emit(SampleTypes);

        // Assert
        result.Should().Contain("execute<K extends keyof AllCommands>");
        result.Should().NotContain("executeVoid");
    }

    [TestMethod]
    public void EmitFetchMethod()
    {
        // Act
        var result = MergeFileEmitter.Emit(SampleTypes);

        // Assert
        result.Should().Contain("fetch<K extends keyof AllQueries>");
    }

    [TestMethod]
    public void EmitPublishMethod()
    {
        // Act
        var result = MergeFileEmitter.Emit(SampleTypes);

        // Assert
        result.Should().Contain("publish<K extends keyof AllEvents>");
        result.Should().Contain("this.transport.sendEvent(type, data)");
    }

    [TestMethod]
    public void EmitOnMethod()
    {
        // Act
        var result = MergeFileEmitter.Emit(SampleTypes);

        // Assert
        result.Should().Contain("on<K extends keyof AllEvents>");
        result.Should().Contain("this.transport.subscribe(");
    }

    [TestMethod]
    public void EmitAllEventsTypeAlias()
    {
        // Act
        var result = MergeFileEmitter.Emit(SampleTypes);

        // Assert
        result.Should().Contain("export type AllEvents = EventMap;");
    }

    [TestMethod]
    public void NotEmitVoidCommandMapReferences()
    {
        // Act
        var result = MergeFileEmitter.Emit(SampleTypes);

        // Assert
        result.Should().NotContain("VoidCommandMap");
        result.Should().NotContain("AllVoidCommands");
    }

    [TestMethod]
    public void ReExportTransports()
    {
        // Act
        var result = MergeFileEmitter.Emit(SampleTypes);

        // Assert
        result.Should().Contain("export { HttpTransport } from './conduit';");
    }

    [TestMethod]
    public void ReExportErrorTypes()
    {
        // Act
        var result = MergeFileEmitter.Emit(SampleTypes);

        // Assert
        result.Should().Contain("ConduitError");
        result.Should().Contain("ConduitErrorDetail");
    }

    [TestMethod]
    public void ImportFromConduitNotDefaultGenerated()
    {
        // Act
        var result = MergeFileEmitter.Emit(SampleTypes);

        // Assert
        result.Should().Contain("from './conduit'");
        result.Should().NotContain("from './default.generated'");
    }

    [TestMethod]
    public void ConditionallyReExportSignalRTransport()
    {
        // Act
        var withSignalR = MergeFileEmitter.Emit(SampleTypes, includeSignalR: true);
        var withoutSignalR = MergeFileEmitter.Emit(SampleTypes, includeSignalR: false);

        // Assert
        withSignalR.Should().Contain("export { SignalRTransport } from './conduit';");
        withoutSignalR.Should().NotContain("export { SignalRTransport }");
    }

    [TestMethod]
    public void EmitTheErrorMapAndDataInterfaces()
    {
        var result = MergeFileEmitter.Emit(SampleTypes, [typeof(MergeFileEmitterShould).Assembly]);

        result.Should().Contain("export interface SampleBlockedException {");
        result.Should().Contain("  total: number;");
        result.Should().Contain("  names: string[];");
        result.Should().Contain("export interface ErrorMap {");
        result.Should().Contain("  'Conduit.TypeGen.Tests.Sample.SampleBlockedException': SampleBlockedException;");
        result.Should().Contain("export function isError<K extends keyof ErrorMap>(");
        result.Should().NotContain("SamplePlainException");
    }

    [TestMethod]
    public void EmitAnEmptyErrorMapWithNoAssemblies() =>
        MergeFileEmitter.Emit(SampleTypes).Should().Contain("export interface ErrorMap {}");

    // A real collision needs two same-short-named public error types in the SAME scanned assembly, and
    // Scan always scans the whole assembly — adding that pair permanently to this project's Sample/ folder
    // would trip the guard for EmitTheErrorMapAndDataInterfaces above (and any other test that scans this
    // assembly), not just this one. So this tests ThrowIfErrorTypeNameCollides directly (internal, exposed
    // to this project via InternalsVisibleTo).
    [TestMethod]
    public void ThrowWhenTwoErrorTypesShareATypeScriptInterfaceName()
    {
        // Arrange
        static ErrorTypeInfo Error(string fullTypeName) => new(
            TypeName: "Blocked", FullTypeName: fullTypeName, Type: typeof(object), Properties: []);
        var errors = new List<ErrorTypeInfo> { Error("Ns.A.Blocked"), Error("Ns.B.Blocked") };

        // Act
        var act = () => MergeFileEmitter.ThrowIfErrorTypeNameCollides(
            errors, new Dictionary<string, string>(StringComparer.Ordinal));

        // Assert
        var thrown = act.Should().Throw<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("Ns.A.Blocked");
        thrown.Which.Message.Should().Contain("Ns.B.Blocked");
    }

    [TestMethod]
    public void ThrowWhenAnErrorTypeCollidesWithAnAlreadyEmittedInterfaceName()
    {
        // Arrange
        var errors = new List<ErrorTypeInfo>
        {
            new(TypeName: "Blocked", FullTypeName: "Ns.Errors.Blocked", Type: typeof(object), Properties: []),
        };
        var emittedTypeNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Blocked"] = "Ns.Dtos.Blocked",
        };

        // Act
        var act = () => MergeFileEmitter.ThrowIfErrorTypeNameCollides(errors, emittedTypeNames);

        // Assert
        var thrown = act.Should().Throw<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("Ns.Errors.Blocked");
        thrown.Which.Message.Should().Contain("Ns.Dtos.Blocked");
    }

    // End-to-end through the public API: no colliding types were added to Sample/ for this — it reuses the
    // real SampleBlockedException (which always emits 'export interface SampleBlockedException') and gives
    // it a same-named, fake ROOT type (a caller-supplied DispatchableTypeInfo is never assembly-scanned, so
    // this can't affect any other test).
    [TestMethod]
    public void ThrowThroughEmitWhenARootTypeCollidesWithAnErrorType()
    {
        // Arrange
        List<DispatchableTypeInfo> typesWithCollidingName =
        [
            new()
            {
                TypeName = "SampleBlockedException",
                FullTypeName = "MyApp.Commands.SampleBlockedException",
                Namespace = "MyApp.Commands",
                Kind = DispatchableKind.Model,
            },
        ];

        // Act
        var act = () => MergeFileEmitter.Emit(typesWithCollidingName, [typeof(MergeFileEmitterShould).Assembly]);

        // Assert
        var thrown = act.Should().Throw<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("Conduit.TypeGen.Tests.Sample.SampleBlockedException");
        thrown.Which.Message.Should().Contain("MyApp.Commands.SampleBlockedException");
    }
}
