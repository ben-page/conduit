using System.Globalization;
using AwesomeAssertions;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class DispatchPropertyCultureResolverShould
{
    private static readonly DispatchPropertyCultureResolver Resolver = new(
        "lang",
        new Dictionary<string, CultureInfo>
        {
            ["en"] = CultureInfo.GetCultureInfo("en-US"),
            ["es"] = CultureInfo.GetCultureInfo("es-MX"),
        });

    private static CultureInfo? ResolveWith(object? value)
        => ResolveWith(new Dictionary<string, object?> { ["lang"] = value });

    private static CultureInfo? ResolveWith(IReadOnlyDictionary<string, object?> properties)
    {
        var previous = MessageContext.Current;
        try
        {
            var context = MessageContext.StartDispatchFromTransport("m", null, "c", properties);
            return Resolver.Resolve(context);
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }
    }

    [TestMethod]
    public void ResolveAMappedValue()
    {
        // Arrange / Act
        var culture = ResolveWith("es");

        // Assert
        culture.Should().Be(CultureInfo.GetCultureInfo("es-MX"));
    }

    [TestMethod]
    public void IgnoreCase()
    {
        // Arrange / Act
        var culture = ResolveWith("ES");

        // Assert
        culture.Should().Be(CultureInfo.GetCultureInfo("es-MX"));
    }

    [TestMethod]
    public void AnswerNullWithNoContext()
    {
        // Arrange / Act
        var culture = Resolver.Resolve(null);

        // Assert
        culture.Should().BeNull();
    }

    [TestMethod]
    public void AnswerNullForAnUnmappedOrForeignValue()
    {
        // Arrange
        object?[] values = ["xx", string.Empty, new string('a', 10_000), 42, null];

        foreach (var value in values)
        {
            // Act
            var culture = ResolveWith(value);

            // Assert
            culture.Should().BeNull($"{value?.GetType().Name ?? "null"} is not a mapped value");
        }
    }

    [TestMethod]
    public void AnswerNullWithoutTheProperty()
    {
        // Arrange / Act
        var culture = ResolveWith(new Dictionary<string, object?> { ["other"] = "es" });

        // Assert
        culture.Should().BeNull();
    }

    [TestMethod]
    public void RefuseAMapWithTwoKeysThatDifferOnlyByCase()
    {
        // Arrange
        var map = new Dictionary<string, CultureInfo>
        {
            ["es"] = CultureInfo.GetCultureInfo("es-MX"),
            ["ES"] = CultureInfo.GetCultureInfo("es-ES"),
        };

        // Act
        var create = () => new DispatchPropertyCultureResolver("lang", map);

        // Assert
        create.Should().Throw<ArgumentException>().Which.Message.Should().Contain("differ only by case");
    }
}
