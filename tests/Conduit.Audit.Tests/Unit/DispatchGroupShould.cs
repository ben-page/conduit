using AwesomeAssertions;
using Conduit.Audit.Runtime;
using Conduit.Runtime;

namespace Conduit.Audit.Tests.Unit;

/// <summary>
/// A group is added to on the dispatching thread and sealed on another (the stale sweep, or a terminal observation that
/// races it): the seal is a consistent snapshot, and nothing is added after it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DispatchGroupShould
{
    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task TolerateConcurrentAddAndSeal()
    {
        // Arrange
        var now = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        for (var round = 0; round < 50; round++)
        {
            var group = new DispatchGroup(MessageContext.StartDispatch(), "TestService", 0, now);
            var accepted = 0;
            var adding = Task.Run(() =>
            {
                for (var index = 0; index < 1_000_000 && group.Add(new HandlerStarted("BusyHandler"), now); index++)
                {
                    Interlocked.Increment(ref accepted);
                }
            });
            SpinWait.SpinUntil(() => Volatile.Read(ref accepted) > 1_000 || adding.IsCompleted);

            // Act — seal while the other thread is still adding
            var record = group.Seal();
            await adding;
            var addedAfterTheSeal = group.Add(new HandlerStarted("LateHandler"), now);

            // Assert — the record holds every observation accepted, and the group takes none after it
            record.Observations.Should().HaveCount(Volatile.Read(ref accepted));
            addedAfterTheSeal.Should().BeFalse();
        }
    }
}
