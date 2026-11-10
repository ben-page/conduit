using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

[TestClass]
public class BrokerTransportOptionsShould
{
    // Zero, a negative value, Timeout.InfiniteTimeSpan (-1 ms), TimeSpan.MaxValue and 1 ms beyond the longest delay a
    // timer accepts: a request would time out at once, never, or fail on a timer that refuses the delay. The
    // registration fails instead, naming the endpoint.
    [TestMethod]
    [DataRow(0L)]
    [DataRow(-10_000_000L)]
    [DataRow(-10_000L)]
    [DataRow(long.MaxValue)]
    [DataRow(42_949_672_950_000L)]
    public void RejectANonPositiveOrInfiniteResponseTimeout(long responseTimeoutTicks)
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new FakeBrokerOptions { ResponseTimeout = TimeSpan.FromTicks(responseTimeoutTicks) };

        // Act
        var register = () => services.AddConduit(conduit =>
            conduit.AddEndpoint("orders", endpoint => endpoint.UseTransport<FakeBrokerTransport>(options)));

        // Assert
        register.Should().Throw<InvalidOperationException>()
            .WithMessage("*'orders'*ResponseTimeout*");
    }
}
