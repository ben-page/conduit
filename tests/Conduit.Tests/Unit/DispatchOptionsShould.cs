using AwesomeAssertions;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class DispatchOptionsShould
{
    [TestMethod]
    public void TagPropertiesViaFluentApi()
    {
        // Arrange & Act
        var options = new DispatchOptions()
            .Tag("partner", "ACME")
            .Tag("document", "invoice-12345");

        // Assert
        options.Properties.Should().ContainKey("partner").WhoseValue.Should().Be("ACME");
        options.Properties.Should().ContainKey("document").WhoseValue.Should().Be("invoice-12345");
    }

    [TestMethod]
    public void StartWithEmptyProperties()
    {
        // Arrange & Act
        var options = new DispatchOptions();

        // Assert
        options.Properties.Should().BeEmpty();
    }

    [TestMethod]
    public void OverwriteExistingTag()
    {
        // Arrange
        var options = new DispatchOptions().Tag("key", "first");

        // Act
        options.Tag("key", "second");

        // Assert
        options.Properties["key"].Should().Be("second");
    }

    [TestMethod]
    public void AllowNullValues()
    {
        // Arrange & Act
        var options = new DispatchOptions().Tag("key", null);

        // Assert
        options.Properties.Should().ContainKey("key").WhoseValue.Should().BeNull();
    }
}
