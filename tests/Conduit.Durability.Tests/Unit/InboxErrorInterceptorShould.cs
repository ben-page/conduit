using AwesomeAssertions;
using Conduit.Durability.Runtime.Inbox;
using Conduit.Durability.Tests.Support;
using Conduit.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public class InboxErrorInterceptorShould
{
    private static readonly BinaryData Body = BinaryData.FromString("{}");

    [TestMethod]
    public async Task ReleaseTheRecordItsDispatchMade()
    {
        // Arrange — the gate admitted the message, then its handler failed
        var store = new InMemoryInboxStore();
        var gate = new InboxInterceptor(store);
        var interceptor = new InboxErrorInterceptor(gate, store, NullLogger<InboxErrorInterceptor>.Instance);
        var previous = MessageContext.Current;
        MessageContext.StartDispatchFromTransport("msg-001", null, "conv-001");

        try
        {
            await gate.ShouldProcessAsync("Some.Command", CancellationToken.None);

            // Act
            var captured = await interceptor.TryCaptureAsync(
                "Some.Command", Body, "orders", new InvalidOperationException("failed"), reference: null, CancellationToken.None);

            // Assert — released, and nothing captured: the dead letter queue or the transport's fallback decides
            (await store.ExistsAsync("msg-001", CancellationToken.None)).Should().BeFalse();
            captured.Should().BeFalse();
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }
    }

    // A failure before the gate ran (an unreadable body, a gate ahead of the inbox that threw) or a duplicate the gate
    // discarded: the record belongs to another delivery, perhaps one whose handler succeeded, so it stays.
    [TestMethod]
    public async Task LeaveARecordItsDispatchDidNotMake()
    {
        // Arrange — an earlier delivery recorded the id; this dispatch's gate found it a duplicate
        var store = new InMemoryInboxStore();
        await store.RecordAsync("msg-001", CancellationToken.None);
        var gate = new InboxInterceptor(store);
        var interceptor = new InboxErrorInterceptor(gate, store, NullLogger<InboxErrorInterceptor>.Instance);
        var previous = MessageContext.Current;
        MessageContext.StartDispatchFromTransport("msg-001", null, "conv-001");

        try
        {
            await gate.ShouldProcessAsync("Some.Command", CancellationToken.None);

            // Act
            var captured = await interceptor.TryCaptureAsync(
                "Some.Command", Body, "orders", new InvalidOperationException("failed"), reference: null, CancellationToken.None);

            // Assert
            (await store.ExistsAsync("msg-001", CancellationToken.None)).Should().BeTrue();
            captured.Should().BeFalse();
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }
    }

    // The interceptor is offered a failure in the transport's error path: a store that cannot remove is logged, never
    // a second failure for the transport to handle.
    [TestMethod]
    public async Task NotThrowWhenTheStoreCannotRelease()
    {
        // Arrange
        var store = new InMemoryInboxStore { RemoveFailure = new InvalidOperationException("The database is down.") };
        var gate = new InboxInterceptor(store);
        var interceptor = new InboxErrorInterceptor(gate, store, NullLogger<InboxErrorInterceptor>.Instance);
        var previous = MessageContext.Current;
        MessageContext.StartDispatchFromTransport("msg-001", null, "conv-001");

        try
        {
            await gate.ShouldProcessAsync("Some.Command", CancellationToken.None);

            // Act
            var capture = () => interceptor.TryCaptureAsync(
                "Some.Command", Body, "orders", new InvalidOperationException("failed"), reference: null, CancellationToken.None);

            // Assert
            (await capture.Should().NotThrowAsync()).Subject.Should().BeFalse();
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }
    }
}
