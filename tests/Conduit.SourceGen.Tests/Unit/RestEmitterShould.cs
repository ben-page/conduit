using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;
using Microsoft.CodeAnalysis;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class RestEmitterShould : GeneratorTestBase
{
    private const string QuerySource = """
        using Conduit.Messages;

        namespace App.Models
        {
            public sealed record ItemDto(System.Guid Id);
        }

        namespace App.Queries
        {
            public sealed record GetItemQuery(System.Guid Id) : IQuery<App.Models.ItemDto>;
        }
        """;

    private const string CommandSource = """
        using Conduit.Messages;

        namespace App.Commands;

        public sealed record CreateItemCommand(string Name) : ICommand<System.Guid>;
        """;

    private const string VoidCommandSource = """
        using Conduit.Messages;

        namespace App.Commands;

        public sealed record DeleteItemCommand(System.Guid Id) : ICommand;
        """;

    [TestMethod]
    public void EmitMapConduitGetForQueryTypes()
    {
        // Act
        var source = EmitRestEndpoints(QuerySource);

        // Assert
        source.Should().Contain("MapConduitGet<T>");
        source.Should().Contain("typeof(T) == typeof(App.Queries.GetItemQuery)");
        source.Should().Contain("ConduitRestHandlers.MapQuery<App.Queries.GetItemQuery, App.Models.ItemDto>(endpoints, pattern, \"GET\")");
    }

    [TestMethod]
    public void EmitMapConduitPostForCommandTypes()
    {
        // Act
        var source = EmitRestEndpoints(CommandSource);

        // Assert
        source.Should().Contain("MapConduitPost<T>");
        source.Should().Contain("ConduitRestHandlers.MapCommandBody<App.Commands.CreateItemCommand, System.Guid>(endpoints, pattern, \"POST\")");
    }

    [TestMethod]
    public void EmitMapConduitDeleteForVoidCommandTypes()
    {
        // Act
        var source = EmitRestEndpoints(VoidCommandSource);

        // Assert
        source.Should().Contain("MapConduitDelete<T>");
        source.Should().Contain("ConduitRestHandlers.MapVoidCommandRoute<App.Commands.DeleteItemCommand>(endpoints, pattern, \"DELETE\")");
    }

    [TestMethod]
    public void EmitGenericMapConduitEscapeHatch()
    {
        // Act
        var source = EmitRestEndpoints(QuerySource);

        // Assert
        source.Should().Contain("MapConduit<T>(");
        source.Should().Contain("string method, string pattern");
    }

    [TestMethod]
    public void NotIncludeCommandsInMapConduitGet()
    {
        // Act
        var source = EmitRestEndpoints(CommandSource);

        // Assert — MapConduitGet should NOT contain command mappings
        var getMethod = ExtractMethod(source, "MapConduitGet");
        getMethod.Should().NotContain("CreateItemCommand");
    }

    [TestMethod]
    public void NotIncludeEventsInAnyMethod()
    {
        // Arrange
        const string eventSource = """
            using Conduit.Messages;

            namespace App.Events;

            public sealed record ItemCreatedEvent(System.Guid Id) : IEvent;
            """;

        // Act
        var source = EmitRestEndpoints(eventSource);

        // Assert
        source.Should().NotContain("ItemCreatedEvent");
    }

    [TestMethod]
    public void EmitInMicrosoftAspNetCoreBuilderNamespace()
    {
        // Act
        var source = EmitRestEndpoints(QuerySource);

        // Assert
        source.Should().Contain("namespace Microsoft.AspNetCore.Builder;");
    }

    [TestMethod]
    public void UseBodyBindingForPostAndRouteBindingForDelete()
    {
        // Act
        var source = EmitRestEndpoints(VoidCommandSource);

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
        var (output, _) = RunGenerator(source);

        // Assert
        var restSource = FindGeneratedSource(output, "ConduitRestEndpoints");
        restSource.Should().BeNull();
    }

    [TestMethod]
    public void EmitTheEndpointsClassInternal()
    {
        // Arrange -- a module assembly that sees Conduit.Transport.Http runs the generator too, so it carries its own
        // generated endpoints class in Microsoft.AspNetCore.Builder
        const string moduleSource = """
            using Conduit.Messages;

            namespace Module.Queries;

            public sealed record GetModuleItem(int Id) : IQuery<string>;
            """;
        var (module, _) = RunGenerator(moduleSource, HttpTransportReference);
        using var moduleImage = new MemoryStream();
        module.WithAssemblyName("Module").Emit(moduleImage).Success.Should().BeTrue();
        const string hostSource = """
            using Conduit.Messages;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;

            namespace Host
            {
                public sealed record GetHostItem(int Id) : IQuery<string>;

                public static class Endpoints
                {
                    public static void Map(IEndpointRouteBuilder endpoints)
                    {
                        endpoints.MapConduitGet<GetHostItem>("/host-items/{id}");
                        endpoints.MapConduitGet<Module.Queries.GetModuleItem>("/module-items/{id}");
                    }
                }
            }
            """;

        // Act -- RunGenerator asserts the host compiles: with two public classes the extension call is ambiguous (CS0121)
        var (host, _) = RunGenerator(hostSource, HttpTransportReference, MetadataReference.CreateFromImage(moduleImage.ToArray()));

        // Assert
        FindGeneratedSource(host, "ConduitRestEndpoints").Should().Contain("internal static class ConduitRestEndpoints");
    }

    // Runs the generator with Conduit.Transport.Http referenced, so the REST endpoints are emitted and compiled.
    private static string EmitRestEndpoints(string source)
    {
        var (output, _) = RunGenerator(source, HttpTransportReference);
        var restSource = FindGeneratedSource(output, "ConduitRestEndpoints");
        restSource.Should().NotBeNull();
        return restSource;
    }

    private static string ExtractMethod(string source, string methodName)
    {
        var start = source.IndexOf($"{methodName}<T>(", StringComparison.Ordinal);
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
