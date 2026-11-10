using System.Text;
using AwesomeAssertions;
using Conduit.Runtime;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Tests.Unit;

[TestClass]
public class ReplyCorrelatorShould
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task CompleteAWaiter()
    {
        // Arrange
        var correlator = new ReplyCorrelator(new FakeTimeProvider());
        using var registration = correlator.Register("reply-1", "Orders.PlaceOrder", "orders");
        var reply = correlator.WaitAsync("reply-1", ResponseTimeout, CancellationToken.None);

        // Act
        var completed = correlator.Complete("reply-1", Encoding.UTF8.GetBytes("{\"data\":1}"));

        // Assert
        completed.Should().BeTrue();
        Encoding.UTF8.GetString((await reply).Span).Should().Be("{\"data\":1}");
    }

    [TestMethod]
    public async Task KeepAReplyThatArrivesBeforeTheWait()
    {
        // Arrange -- the reply races the publish's completion: it arrives before anyone awaits it
        var correlator = new ReplyCorrelator(new FakeTimeProvider());
        using var registration = correlator.Register("reply-1", "Orders.PlaceOrder", "orders");
        correlator.Complete("reply-1", Encoding.UTF8.GetBytes("early"));

        // Act
        var reply = await correlator.WaitAsync("reply-1", ResponseTimeout, CancellationToken.None);

        // Assert
        Encoding.UTF8.GetString(reply.Span).Should().Be("early");
    }

    [TestMethod]
    public void IgnoreAReplyNobodyAwaits()
    {
        // Arrange -- a late reply, after its request timed out and was released
        var correlator = new ReplyCorrelator(new FakeTimeProvider());
        correlator.Register("reply-1", "Orders.PlaceOrder", "orders").Dispose();

        // Act
        var completed = correlator.Complete("reply-1", Encoding.UTF8.GetBytes("late"));

        // Assert
        completed.Should().BeFalse();
        correlator.PendingCount.Should().Be(0);
    }

    [TestMethod]
    public async Task TimeOutWithTimeoutException()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var correlator = new ReplyCorrelator(clock);
        using var registration = correlator.Register("reply-1", "Orders.PlaceOrder", "orders");
        var reply = correlator.WaitAsync("reply-1", ResponseTimeout, CancellationToken.None);

        // Act
        clock.Advance(ResponseTimeout);

        // Assert -- a TimeoutException, never an OperationCanceledException a caller's catch would swallow
        var exception = await reply.Awaiting(task => task).Should().ThrowExactlyAsync<TimeoutException>();
        exception.WithMessage("No reply to 'Orders.PlaceOrder' from endpoint 'orders' within 00:00:05.");
    }

    [TestMethod]
    public async Task PropagateTheCallersCancellation()
    {
        // Arrange
        var correlator = new ReplyCorrelator(new FakeTimeProvider());
        using var registration = correlator.Register("reply-1", "Orders.PlaceOrder", "orders");
        using var caller = new CancellationTokenSource();
        var reply = correlator.WaitAsync("reply-1", ResponseTimeout, caller.Token);

        // Act
        await caller.CancelAsync();

        // Assert -- the caller's own token, so the caller can tell it gave up
        var exception = await reply.Awaiting(task => task).Should().ThrowAsync<OperationCanceledException>();
        exception.Which.CancellationToken.Should().Be(caller.Token);
    }

    [TestMethod]
    public async Task FailAllOnStop()
    {
        // Arrange
        var correlator = new ReplyCorrelator(new FakeTimeProvider());
        using var first = correlator.Register("reply-1", "Orders.PlaceOrder", "orders");
        using var second = correlator.Register("reply-2", "Orders.CancelOrder", "orders");
        var firstReply = correlator.WaitAsync("reply-1", ResponseTimeout, CancellationToken.None);
        var secondReply = correlator.WaitAsync("reply-2", ResponseTimeout, CancellationToken.None);
        var stopped = new InvalidOperationException("stopped");

        // Act
        correlator.FailAll(stopped);

        // Assert
        (await firstReply.Awaiting(task => task).Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(stopped);
        (await secondReply.Awaiting(task => task).Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(stopped);
    }

    [TestMethod]
    public void ReleaseTheWaiterWhenTheRequestEnds()
    {
        // Arrange
        var correlator = new ReplyCorrelator(new FakeTimeProvider());
        var registration = correlator.Register("reply-1", "Orders.PlaceOrder", "orders");

        // Act
        registration.Dispose();

        // Assert
        correlator.PendingCount.Should().Be(0);
    }

    [TestMethod]
    public async Task RefuseToWaitForAnUnregisteredRequest()
    {
        // Arrange
        var correlator = new ReplyCorrelator(new FakeTimeProvider());

        // Act
        var wait = () => correlator.WaitAsync("never-registered", ResponseTimeout, CancellationToken.None);

        // Assert
        await wait.Should().ThrowAsync<InvalidOperationException>();
    }
}
