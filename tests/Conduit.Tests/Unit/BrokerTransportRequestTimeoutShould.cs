using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

/// <summary>
/// A request's whole round trip, its publish included, is bounded by the response timeout: a publish the
/// broker never confirms fails as a missing reply does, never hangs.
/// </summary>
[TestClass]
public class BrokerTransportRequestTimeoutShould
{
    [TestMethod]
    [Timeout(10_000)]
    public async Task TimeOutAPublishThatNeverCompletes()
    {
        // Arrange — a broker that never confirms the request's publish
        await using var services = new ServiceCollection().BuildServiceProvider();
        var transport = new FakeBrokerTransport(
            new ConduitOptions(),
            services,
            new FakeBrokerOptions { ResponseTimeout = TimeSpan.FromMilliseconds(300) })
        {
            PublishRequestsHang = true,
        };
        transport.Start();
        var envelope = TestEnvelope.ForRequest(new CreateItemCommand("unconfirmed"));

        // Act
        var send = () => transport.SendRequestAsync(envelope, CancellationToken.None);

        // Assert — the same TimeoutException as a reply that never comes, never an OperationCanceledException
        var exception = await send.Should().ThrowExactlyAsync<TimeoutException>();
        exception.WithMessage($"No reply to '{typeof(CreateItemCommand).FullName}' from endpoint '' within 00:00:00.3000000.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task CancelAHungPublishWithTheCallersOwnToken()
    {
        // Arrange — the publish hangs, and the caller gives up long before the response timeout
        await using var services = new ServiceCollection().BuildServiceProvider();
        var transport = new FakeBrokerTransport(
            new ConduitOptions(),
            services,
            new FakeBrokerOptions { ResponseTimeout = TimeSpan.FromMinutes(1) })
        {
            PublishRequestsHang = true,
        };
        transport.Start();
        var envelope = TestEnvelope.ForRequest(new CreateItemCommand("abandoned"));
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        var send = () => transport.SendRequestAsync(envelope, caller.Token);

        // Assert — the caller's own cancellation stays a cancellation, carrying the caller's token
        (await send.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should().Be(caller.Token);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task TimeOutAPublishThatIgnoresItsToken()
    {
        // Arrange — a broker client stuck in a call that takes no cancellation token
        await using var services = new ServiceCollection().BuildServiceProvider();
        var transport = new FakeBrokerTransport(
            new ConduitOptions(),
            services,
            new FakeBrokerOptions { ResponseTimeout = TimeSpan.FromMilliseconds(300) })
        {
            PublishRequestsIgnoreTheirToken = true,
        };
        transport.Start();
        var envelope = TestEnvelope.ForRequest(new CreateItemCommand("stuck"));

        // Act
        var send = () => transport.SendRequestAsync(envelope, CancellationToken.None);

        // Assert — released at the response timeout all the same, with the missing reply's exception
        var exception = await send.Should().ThrowExactlyAsync<TimeoutException>();
        exception.WithMessage($"No reply to '{typeof(CreateItemCommand).FullName}' from endpoint '' within 00:00:00.3000000.");
    }
}
