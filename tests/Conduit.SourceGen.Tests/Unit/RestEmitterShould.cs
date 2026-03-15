using AwesomeAssertions;
using Conduit.Analysis;
using Conduit.SourceGen.Tests.Support;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class RestEmitterShould : GeneratorTestBase
{
    [TestMethod]
    public void EmitMapConduitGetForQueryTypes()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            MakeQuery("App.Queries.GetItemQuery", "App.Models.ItemDto"),
        };

        // Act
        var source = RestEmitter.Emit(types);

        // Assert
        source.Should().Contain("MapConduitGet<T>");
        source.Should().Contain("typeof(T) == typeof(App.Queries.GetItemQuery)");
        source.Should().Contain("ConduitRestHandlers.MapQuery<App.Queries.GetItemQuery, App.Models.ItemDto>(endpoints, pattern, \"GET\")");
    }

    [TestMethod]
    public void EmitMapConduitPostForCommandTypes()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            MakeCommand("App.Commands.CreateItemCommand", "System.Guid"),
        };

        // Act
        var source = RestEmitter.Emit(types);

        // Assert
        source.Should().Contain("MapConduitPost<T>");
        source.Should().Contain("ConduitRestHandlers.MapCommandBody<App.Commands.CreateItemCommand, System.Guid>(endpoints, pattern, \"POST\")");
    }

    [TestMethod]
    public void EmitMapConduitDeleteForVoidCommandTypes()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            MakeVoidCommand("App.Commands.DeleteItemCommand"),
        };

        // Act
        var source = RestEmitter.Emit(types);

        // Assert
        source.Should().Contain("MapConduitDelete<T>");
        source.Should().Contain("ConduitRestHandlers.MapVoidCommandRoute<App.Commands.DeleteItemCommand>(endpoints, pattern, \"DELETE\")");
    }

    [TestMethod]
    public void EmitGenericMapConduitEscapeHatch()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            MakeQuery("App.Queries.GetItemQuery", "App.Models.ItemDto"),
        };

        // Act
        var source = RestEmitter.Emit(types);

        // Assert
        source.Should().Contain("MapConduit<T>(");
        source.Should().Contain("string method, string pattern");
    }

    [TestMethod]
    public void NotIncludeCommandsInMapConduitGet()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            MakeCommand("App.Commands.CreateItemCommand", "System.Guid"),
        };

        // Act
        var source = RestEmitter.Emit(types);

        // Assert — MapConduitGet should NOT contain command mappings
        var getMethod = ExtractMethod(source, "MapConduitGet");
        getMethod.Should().NotContain("CreateItemCommand");
    }

    [TestMethod]
    public void NotIncludeEventsInAnyMethod()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            new()
            {
                TypeName = "ItemCreatedEvent",
                FullTypeName = "App.Events.ItemCreatedEvent",
                Namespace = "App.Events",
                Kind = DispatchableKind.Event,
                HasValidateOverride = false,
                HasAuthorizeAttribute = false,
                HasAllowAnonymousAttribute = false,
                IsLocalOnly = false,
            },
        };

        // Act
        var source = RestEmitter.Emit(types);

        // Assert
        source.Should().NotContain("ItemCreatedEvent");
    }

    [TestMethod]
    public void EmitInMicrosoftAspNetCoreBuilderNamespace()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            MakeQuery("App.Queries.GetItemQuery", "App.Models.ItemDto"),
        };

        // Act
        var source = RestEmitter.Emit(types);

        // Assert
        source.Should().Contain("namespace Microsoft.AspNetCore.Builder;");
    }

    [TestMethod]
    public void UseBodyBindingForPostAndRouteBindingForDelete()
    {
        // Arrange
        var types = new List<DispatchableTypeInfo>
        {
            MakeVoidCommand("App.Commands.DeleteItemCommand"),
        };

        // Act
        var source = RestEmitter.Emit(types);

        // Assert — POST uses Body, DELETE uses Route
        source.Should().Contain("MapVoidCommandBody<App.Commands.DeleteItemCommand>(endpoints, pattern, \"POST\")");
        source.Should().Contain("MapVoidCommandRoute<App.Commands.DeleteItemCommand>(endpoints, pattern, \"DELETE\")");
    }

    [TestMethod]
    public void NotEmitRestEndpointsWhenTransportHttpNotReferenced()
    {
        // Arrange — standard source with a query but no Transport.Http reference
        var source = @"
using Conduit.Messages;
namespace App.Queries;
public record GetItemQuery(System.Guid Id) : IQuery<string>;
";

        // Act
        var (generatedTrees, _) = RunGenerator(source);

        // Assert
        var restSource = FindGeneratedSource(generatedTrees, "ConduitRestEndpoints");
        restSource.Should().BeNull();
    }

    private static DispatchableTypeInfo MakeQuery(string fullTypeName, string resultFullTypeName)
    {
        return new()
        {
            TypeName = fullTypeName.Split('.').Last(),
            FullTypeName = fullTypeName,
            Namespace = string.Join(".", fullTypeName.Split('.').Reverse().Skip(1).Reverse()),
            Kind = DispatchableKind.Query,
            ResultTypeName = resultFullTypeName.Split('.').Last(),
            ResultFullTypeName = resultFullTypeName,
            HasValidateOverride = false,
            HasAuthorizeAttribute = false,
            HasAllowAnonymousAttribute = false,
        };
    }

    private static DispatchableTypeInfo MakeCommand(string fullTypeName, string resultFullTypeName)
    {
        return new()
        {
            TypeName = fullTypeName.Split('.').Last(),
            FullTypeName = fullTypeName,
            Namespace = string.Join(".", fullTypeName.Split('.').Reverse().Skip(1).Reverse()),
            Kind = DispatchableKind.Command,
            ResultTypeName = resultFullTypeName.Split('.').Last(),
            ResultFullTypeName = resultFullTypeName,
            HasValidateOverride = false,
            HasAuthorizeAttribute = false,
            HasAllowAnonymousAttribute = false,
        };
    }

    private static DispatchableTypeInfo MakeVoidCommand(string fullTypeName)
    {
        return new()
        {
            TypeName = fullTypeName.Split('.').Last(),
            FullTypeName = fullTypeName,
            Namespace = string.Join(".", fullTypeName.Split('.').Reverse().Skip(1).Reverse()),
            Kind = DispatchableKind.VoidCommand,
            HasValidateOverride = false,
            HasAuthorizeAttribute = false,
            HasAllowAnonymousAttribute = false,
        };
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var start = source.IndexOf($"{methodName}<T>(", System.StringComparison.Ordinal);
        if (start == -1)
        {
            return string.Empty;
        }

        var braceCount = 0;
        var methodStart = source.IndexOf('{', start);
        for (var i = methodStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                braceCount++;
            }

            if (source[i] == '}')
            {
                braceCount--;
                if (braceCount == 0)
                {
                    return source[start..(i + 1)];
                }
            }
        }

        return source[start..];
    }
}
