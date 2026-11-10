using Amqp;
using AwesomeAssertions;

namespace Conduit.Transport.ActiveMQ.Tests.Integration;

/// <summary>
/// Tests the cache of reply sender links against the real broker: a reply address is the requester's to
/// choose, so the cache must stay bounded however many distinct ones arrive.
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
            using var replySenders = new ActiveMQReplySenders(new Session(connection));
            var addresses = Enumerable.Range(0, 70).Select(index => $"test-reply-{Guid.NewGuid():N}-{index}").ToList();
            var links = new List<SenderLink>();
            for (var index = 0; index < 64; index++)
            {
                links.Add(await replySenders.GetAsync(addresses[index]));
            }

            var firstAgain = await replySenders.GetAsync(addresses[0]);

            // Act — six more distinct addresses
            var counts = new List<int>();
            for (var index = 64; index < 70; index++)
            {
                links.Add(await replySenders.GetAsync(addresses[index]));
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
}
