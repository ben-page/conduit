using System.Text;
using AwesomeAssertions;
using Conduit.Analysis;

namespace Conduit.TypeGen.Tests.Unit;

[TestClass]
public class TypeMapEmitterShould
{
    [TestMethod]
    public void UseFullTypeNameAsCommandMapKey()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            new()
            {
                TypeName = "CreateCommand",
                FullTypeName = "MyApp.Commands.CreateCommand",
                Namespace = "MyApp.Commands",
                Kind = DispatchableKind.Command,
                ResultTypeName = "Guid",
                ResultFullTypeName = "System.Guid",
            }
        };
        var sb = new StringBuilder();

        // Act
        TypeMapEmitter.EmitMaps(sb, types);

        // Assert
        var result = sb.ToString();
        result.Should().Contain("'MyApp.Commands.CreateCommand':");
    }

    [TestMethod]
    public void MergeVoidCommandsIntoCommandMap()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            new()
            {
                TypeName = "DeleteCommand",
                FullTypeName = "MyApp.Commands.DeleteCommand",
                Namespace = "MyApp.Commands",
                Kind = DispatchableKind.VoidCommand,
            }
        };
        var sb = new StringBuilder();

        // Act
        TypeMapEmitter.EmitMaps(sb, types);

        // Assert
        var result = sb.ToString();
        result.Should().Contain("export interface CommandMap");
        result.Should().Contain("response: void");
        result.Should().NotContain("VoidCommandMap");
    }

    [TestMethod]
    public void EmitBothCommandTypesInSameMap()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
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
                TypeName = "DeleteCommand",
                FullTypeName = "MyApp.Commands.DeleteCommand",
                Namespace = "MyApp.Commands",
                Kind = DispatchableKind.VoidCommand,
            }
        };
        var sb = new StringBuilder();

        // Act
        TypeMapEmitter.EmitMaps(sb, types);

        // Assert
        var result = sb.ToString();
        result.Should().Contain("'MyApp.Commands.CreateCommand': { request: CreateCommand; response: string };");
        result.Should().Contain("'MyApp.Commands.DeleteCommand': { request: DeleteCommand; response: void };");
    }

    [TestMethod]
    public void UseFullTypeNameAsQueryMapKey()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            new()
            {
                TypeName = "GetItemsQuery",
                FullTypeName = "MyApp.Queries.GetItemsQuery",
                Namespace = "MyApp.Queries",
                Kind = DispatchableKind.Query,
                ResultTypeName = "String",
                ResultFullTypeName = "System.String",
            }
        };
        var sb = new StringBuilder();

        // Act
        TypeMapEmitter.EmitMaps(sb, types);

        // Assert
        var result = sb.ToString();
        result.Should().Contain("'MyApp.Queries.GetItemsQuery':");
    }

    [TestMethod]
    public void UseFullTypeNameAsEventMapKey()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            new()
            {
                TypeName = "ItemCreatedEvent",
                FullTypeName = "MyApp.Events.ItemCreatedEvent",
                Namespace = "MyApp.Events",
                Kind = DispatchableKind.Event,
            }
        };
        var sb = new StringBuilder();

        // Act
        TypeMapEmitter.EmitMaps(sb, types);

        // Assert
        var result = sb.ToString();
        result.Should().Contain("'MyApp.Events.ItemCreatedEvent': ItemCreatedEvent;");
    }

    [TestMethod]
    public void NotEmitVoidCommandMap()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            new()
            {
                TypeName = "DeleteCommand",
                FullTypeName = "MyApp.Commands.DeleteCommand",
                Namespace = "MyApp.Commands",
                Kind = DispatchableKind.VoidCommand,
            }
        };
        var sb = new StringBuilder();

        // Act
        TypeMapEmitter.EmitMaps(sb, types);

        // Assert
        sb.ToString().Should().NotContain("VoidCommandMap");
    }

    [TestMethod]
    public void NotEmitModelTypesInMaps()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            new()
            {
                TypeName = "ItemDto",
                FullTypeName = "MyApp.Models.ItemDto",
                Namespace = "MyApp.Models",
                Kind = DispatchableKind.Model,
            }
        };
        var sb = new StringBuilder();

        // Act
        TypeMapEmitter.EmitMaps(sb, types);

        // Assert
        var result = sb.ToString();
        result.Should().NotContain("CommandMap");
        result.Should().NotContain("QueryMap");
        result.Should().NotContain("EventMap");
        result.Should().NotContain("ItemDto");
    }
}
