using Amqp;
using Amqp.Framing;
using AwesomeAssertions;

namespace Conduit.Transport.ActiveMQ.Tests.Integration;

/// <summary>
/// Tests the cache of reply sender links against the real broker: a reply address is the requester's to
/// choose, so the cache must stay bounded however many distinct ones arrive, and a failed reply is never
/// retried, so no link may be closed under a send.
/// </summary>
[TestClass]
public class ActiveMQReplySendersShould : ActiveMQTestBase
{
    [TestMethod]
    public async Task CapTheCachedSendersAndEvictTheLeastRecentlyUsed()
    {
        // Arrange — 64 reply addresses cached, then the first used again, so the second is the least recent
        var connection = await new ConnectionFactory().CreateAsync(new Address(ConnectionString!));
        try
        {
            var replySenders = new ActiveMQReplySenders(new Session(connection));
            var addresses = UniqueAddresses(70);
            var links = new List<SenderLink>();
            for (var index = 0; index < 64; index++)
            {
                links.Add(Use(replySenders, addresses[index]));
            }

            var firstAgain = Use(replySenders, addresses[0]);

            // Act — six more distinct addresses
            var counts = new List<int>();
            for (var index = 64; index < 70; index++)
            {
                links.Add(Use(replySenders, addresses[index]));
                counts.Add(replySenders.Count);
            }

            // Assert — never more than 64 cached; the six least recently used (the second to the seventh
            // address) were evicted and their links closed, while the first, used again, kept its link
            counts.Should().AllSatisfy(count => count.Should().BeLessThanOrEqualTo(64));
            replySenders.Count.Should().Be(64);
            links.Skip(1).Take(6).Should().AllSatisfy(link => link.IsClosed.Should().BeTrue());
            firstAgain.Should().BeSameAs(links[0]);
            links[0].IsClosed.Should().BeFalse();
            links.Skip(7).Should().AllSatisfy(link => link.IsClosed.Should().BeFalse());
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [TestMethod]
    public async Task NeverEvictALinkWithASendInFlight()
    {
        // Arrange — 64 cached links; the least recently used one is still held by a send
        var connection = await new ConnectionFactory().CreateAsync(new Address(ConnectionString!));
        try
        {
            var replySenders = new ActiveMQReplySenders(new Session(connection));
            var addresses = UniqueAddresses(65);
            var sending = replySenders.Acquire(addresses[0]);
            var idle = Enumerable.Range(1, 63).Select(index => Use(replySenders, addresses[index])).ToList();

            // Act — a new address needs room
            var lease = replySenders.Acquire(addresses[64]);
            lease.Release();

            // Assert — the next least recently used, idle, link made room; the one under a send stayed open
            replySenders.Count.Should().Be(64);
            sending.Link.IsClosed.Should().BeFalse();
            idle[0].IsClosed.Should().BeTrue();
            idle.Skip(1).Should().AllSatisfy(link => link.IsClosed.Should().BeFalse());
            sending.Release();
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    [TestMethod]
    public async Task SendThroughALinkOfItsOwnWhenEveryCachedLinkIsBusy()
    {
        // Arrange — 64 cached links, every one held by a send, and a requester's real temporary reply queue
        var connection = await new ConnectionFactory().CreateAsync(new Address(ConnectionString!));
        var requester = await new ConnectionFactory().CreateAsync(new Address(ConnectionString!));
        try
        {
            var replySenders = new ActiveMQReplySenders(new Session(connection));
            var busy = UniqueAddresses(64).Select(replySenders.Acquire).ToList();

            var attached = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replyReceiver = new ReceiverLink(
                new Session(requester),
                $"test-reply-{Guid.NewGuid():N}",
                new Source { Dynamic = true },
                (link, attach) => attached.TrySetResult(((Source)attach.Source).Address));
            var replyAddress = await attached.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Act
            var lease = replySenders.Acquire(replyAddress);
            using (var reply = new Message("the reply"))
            {
                await lease.Link.SendAsync(reply);
            }

            lease.Release();

            // Assert — the reply went out on a link of its own, closed once sent, and no busy link was touched
            var received = await replyReceiver.ReceiveAsync(TimeSpan.FromSeconds(10));
            received.Should().NotBeNull();
            received!.Body.Should().Be("the reply");
            lease.IsCached.Should().BeFalse();
            lease.Link.IsClosed.Should().BeTrue();
            replySenders.Count.Should().Be(64);
            busy.Should().AllSatisfy(held => held.Link.IsClosed.Should().BeFalse());
        }
        finally
        {
            await requester.CloseAsync();
            await connection.CloseAsync();
        }
    }

    [TestMethod]
    public async Task CloseADiscardedLinkOnlyOnceItsSendsDrain()
    {
        // Arrange — two replies to one requester sending on the same cached link
        var connection = await new ConnectionFactory().CreateAsync(new Address(ConnectionString!));
        try
        {
            var replySenders = new ActiveMQReplySenders(new Session(connection));
            var replyAddress = UniqueAddresses(1)[0];
            var failed = replySenders.Acquire(replyAddress);
            var stillSending = replySenders.Acquire(replyAddress);

            // Act — the first one's send fails
            failed.Discard();
            failed.Release();
            var openAfterTheFailure = !failed.Link.IsClosed;
            var next = replySenders.Acquire(replyAddress);
            stillSending.Release();

            // Assert — the link closed only once the other send was done, and the next reply got a fresh one
            stillSending.Link.Should().BeSameAs(failed.Link);
            openAfterTheFailure.Should().BeTrue();
            failed.Link.IsClosed.Should().BeTrue();
            next.Link.Should().NotBeSameAs(failed.Link);
            next.Link.IsClosed.Should().BeFalse();
            next.Release();
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static List<string> UniqueAddresses(int count) =>
        [.. Enumerable.Range(0, count).Select(index => $"test-reply-{Guid.NewGuid():N}-{index}")];

    // Leases a link for a reply whose send is already over
    private static SenderLink Use(ActiveMQReplySenders replySenders, string replyAddress)
    {
        var lease = replySenders.Acquire(replyAddress);
        lease.Release();
        return lease.Link;
    }
}
