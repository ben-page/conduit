using AwesomeAssertions;
using Conduit.Durability.Inbox;
using Conduit.Runtime;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public class InboxInterceptorShould
{
    [TestMethod]
    public async Task AllowNewMessage()
    {
        // Arrange
        var store = new InMemoryInboxStore();
        var interceptor = new InboxInterceptor(store);
        var previous = MessageContext.Current;
        MessageContext.StartDispatchFromTransport("msg-001", null, "conv-001");

        try
        {
            // Act
            var result = await interceptor.ShouldProcessAsync(
                "Some.Command", CancellationToken.None);

            // Assert
            result.Should().BeTrue();
            (await store.ExistsAsync("msg-001", CancellationToken.None)).Should().BeTrue();
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }
    }

    [TestMethod]
    public async Task RejectDuplicateMessage()
    {
        // Arrange
        var store = new InMemoryInboxStore();
        var interceptor = new InboxInterceptor(store);
        await store.RecordAsync("msg-001", CancellationToken.None);
        var previous = MessageContext.Current;
        MessageContext.StartDispatchFromTransport("msg-001", null, "conv-001");

        try
        {
            // Act
            var result = await interceptor.ShouldProcessAsync(
                "Some.Command", CancellationToken.None);

            // Assert
            result.Should().BeFalse();
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }
    }

    [TestMethod]
    public async Task AllowWhenNoMessageContext()
    {
        // Arrange
        var store = new InMemoryInboxStore();
        var interceptor = new InboxInterceptor(store);
        var previous = MessageContext.Current;
        MessageContext.RestoreContext(null);

        try
        {
            // Act
            var result = await interceptor.ShouldProcessAsync(
                "Some.Command", CancellationToken.None);

            // Assert
            result.Should().BeTrue();
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }
    }
}

file class InMemoryInboxStore : IInboxStore
{
    private readonly HashSet<string> messages = [];

    public Task<bool> ExistsAsync(string messageId, CancellationToken cancellationToken)
        => Task.FromResult(this.messages.Contains(messageId));

    public Task RecordAsync(string messageId, CancellationToken cancellationToken)
    {
        this.messages.Add(messageId);
        return Task.CompletedTask;
    }

    public Task DeleteExpiredAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task EnsureTableExistsAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
