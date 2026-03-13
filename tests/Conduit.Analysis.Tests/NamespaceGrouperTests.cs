using AwesomeAssertions;

namespace Conduit.Analysis.Tests;

[TestClass]
public class NamespaceGrouperTests
{
    private static DispatchableTypeInfo MakeType(string ns, string name, DispatchableKind kind = DispatchableKind.Command)
        => new()
        {
            TypeName = name,
            FullTypeName = $"{ns}.{name}",
            Namespace = ns,
            Kind = kind,
            HasValidateOverride = false,
            HasAuthorizeAttribute = false,
            HasAllowAnonymousAttribute = false,
        };

    [TestMethod]
    public void GroupsByDeclaredNamespace()
    {
        var declaredNamespaces = new[] { "App.Orders", "App.Inventory" };
        var types = new[]
        {
            MakeType("App.Orders.Commands", "CreateOrder"),
            MakeType("App.Orders.Queries", "GetOrder", DispatchableKind.Query),
            MakeType("App.Inventory.Commands", "AdjustStock"),
        };

        var groups = NamespaceGrouper.Group(types, declaredNamespaces);

        groups.Should().HaveCount(2);
        groups.First(g => g.Namespace == "App.Orders").Types.Should().HaveCount(2);
        groups.First(g => g.Namespace == "App.Inventory").Types.Should().HaveCount(1);
    }

    [TestMethod]
    public void GeneratesKebabCaseFileName()
    {
        var declaredNamespaces = new[] { "App.Orders" };
        var types = new[] { MakeType("App.Orders.Commands", "CreateOrder") };

        var groups = NamespaceGrouper.Group(types, declaredNamespaces);

        groups[0].FileName.Should().Be("app-orders.generated.ts");
    }

    [TestMethod]
    public void TypesOutsideDeclaredNamespaces_GoToDefaultFile()
    {
        var declaredNamespaces = new[] { "App.Orders" };
        var types = new[]
        {
            MakeType("App.Orders.Commands", "CreateOrder"),
            MakeType("App.Other", "SomeCommand"),
        };

        var groups = NamespaceGrouper.Group(types, declaredNamespaces);

        groups.Should().HaveCount(2);
        groups.First(g => g.Namespace == "default").FileName.Should().Be("default.generated.ts");
    }

    [TestMethod]
    public void NoDeclaredNamespaces_AllGoToDefault()
    {
        var types = new[]
        {
            MakeType("App.Commands", "CreateOrder"),
            MakeType("App.Queries", "GetOrder", DispatchableKind.Query),
        };

        var groups = NamespaceGrouper.Group(types, Array.Empty<string>());

        groups.Should().HaveCount(1);
        groups[0].Namespace.Should().Be("default");
    }
}
