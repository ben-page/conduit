using AwesomeAssertions;
using Conduit.TestUtils;

namespace Conduit.Tests.Unit;

[TestClass]
public class TestTopologyPrefixShould
{
    [TestMethod]
    public void ProduceStableHashForTheSameAssembly()
    {
        // Arrange
        var assembly = typeof(TestTopologyPrefixShould).Assembly;

        // Act + Assert
        TestTopologyPrefix.ProjectHash(assembly)
            .Should().Be(TestTopologyPrefix.ProjectHash(assembly));
    }

    [TestMethod]
    public void ProduceDifferentHashesForDifferentAssemblies()
    {
        // Arrange
        var thisAssembly = typeof(TestTopologyPrefixShould).Assembly;
        var otherAssembly = typeof(TestTopologyPrefix).Assembly; // Conduit.TestUtils

        // Act + Assert
        TestTopologyPrefix.ProjectHash(thisAssembly)
            .Should().NotBe(TestTopologyPrefix.ProjectHash(otherAssembly));
    }

    [TestMethod]
    public void BuildProjectPrefixThatStartsWithTestAndEndsWithHyphen()
    {
        // Arrange
        var assembly = typeof(TestTopologyPrefixShould).Assembly;

        // Act
        var prefix = TestTopologyPrefix.ProjectPrefix(assembly);

        // Assert
        prefix.Should().StartWith("test-").And.EndWith("-");
    }

    [TestMethod]
    public void BuildPerTestPrefixUnderTheProjectPrefixAndUniquePerCall()
    {
        // Arrange
        var assembly = typeof(TestTopologyPrefixShould).Assembly;
        var projectPrefix = TestTopologyPrefix.ProjectPrefix(assembly);

        // Act
        var first = TestTopologyPrefix.NewForTest(assembly);
        var second = TestTopologyPrefix.NewForTest(assembly);

        // Assert
        first.Should().StartWith(projectPrefix);
        second.Should().StartWith(projectPrefix);
        first.Should().NotBe(second);
    }
}
