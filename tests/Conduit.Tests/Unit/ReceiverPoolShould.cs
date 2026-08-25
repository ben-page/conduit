using AwesomeAssertions;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

[TestClass]
public class ReceiverPoolShould
{
    private sealed class TestBrokerOptions : BrokerTransportOptions
    {
    }

    [TestMethod]
    public void ConfigureAnExistingPoolInsteadOfAddingASecond()
    {
        // Arrange
        var options = new TestBrokerOptions();
        options.AddPool("bulk", pool => pool.Concurrency = 50);

        // Act
        options.AddPool("bulk", pool => pool.Handle("Orders.Commands.Import"));

        // Assert
        options.Pools.Should().HaveCount(1);
        options.Pools[0].Concurrency.Should().Be(50);
        options.Pools[0].MessageTypes.Should().ContainSingle().Which.Should().Be("Orders.Commands.Import");
    }

    [TestMethod]
    public void ConfigureAnExistingPoolCaseInsensitively()
    {
        // Arrange
        var options = new TestBrokerOptions();
        options.AddPool("Bulk", pool => pool.Concurrency = 50);

        // Act
        options.AddPool("bulk", pool => pool.Handle("Orders.Commands.Import"));

        // Assert
        options.Pools.Should().HaveCount(1);
        options.Pools[0].Name.Should().Be("Bulk");
        options.Pools[0].Concurrency.Should().Be(50);
        options.Pools[0].MessageTypes.Should().ContainSingle().Which.Should().Be("Orders.Commands.Import");
    }

    [TestMethod]
    public void ResolveTypedHandlesToTheSameNameAsStrings()
    {
        // Arrange
        var typed = new TestBrokerOptions();
        var byType = new TestBrokerOptions();
        var byString = new TestBrokerOptions();

        // Act
        typed.AddPool("a", pool => pool.Handle<ReceiverPoolShould>());
#pragma warning disable CA2263 // Intentionally testing the Type overload in addition to the generic overload
        byType.AddPool("a", pool => pool.Handle(typeof(ReceiverPoolShould)));
#pragma warning restore CA2263
        byString.AddPool("a", pool => pool.Handle("Conduit.Tests.Unit.ReceiverPoolShould"));

        // Assert
        typed.Pools[0].MessageTypes.Should().Equal(byType.Pools[0].MessageTypes);
        typed.Pools[0].MessageTypes.Should().Equal(byString.Pools[0].MessageTypes);
    }

    [TestMethod]
    public void IgnoreADuplicateMessageType()
    {
        // Arrange
        var options = new TestBrokerOptions();

        // Act
        options.AddPool("bulk", pool =>
        {
            pool.Handle("Orders.Commands.Import");
            pool.Handle("Orders.Commands.Import");
            pool.Handle<ReceiverPoolShould>();
#pragma warning disable CA2263 // Intentionally testing the Type overload in addition to the generic overload
            pool.Handle(typeof(ReceiverPoolShould));
#pragma warning restore CA2263
        });

        // Assert
        options.Pools[0].MessageTypes.Should().HaveCount(2);
    }

    [TestMethod]
    public void RejectATypeWithNoFullName()
    {
        // Arrange
        var options = new TestBrokerOptions();
        var openGeneric = typeof(List<>).GetGenericArguments()[0];

        // Act
        var act = () => options.AddPool("bulk", pool => pool.Handle(openGeneric));

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
