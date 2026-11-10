using AwesomeAssertions;
using Conduit.Durability.Runtime.DeadLetterQueue;
using Conduit.Durability.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public sealed class DeadLetterErrorInterceptorShould
{
    [TestMethod]
    public async Task StampACaptureWithTheInjectedClock()
    {
        // Arrange — a clock years away from the machine's
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new StubDeadLetterStore();
        var interceptor = new DeadLetterErrorInterceptor(store, NullLogger<DeadLetterErrorInterceptor>.Instance, clock);

        // Act
        var captured = await interceptor.TryCaptureAsync(
            "Orders.Commands.CreateOrder",
            BinaryData.FromString("{}"),
            "orders",
            new InvalidOperationException("the handler failed"),
            reference: null,
            CancellationToken.None);

        // Assert
        captured.Should().BeTrue();
        store.Entries.Should().ContainSingle().Which.DeadLetteredAt.Should().Be(clock.GetUtcNow());
    }
}
