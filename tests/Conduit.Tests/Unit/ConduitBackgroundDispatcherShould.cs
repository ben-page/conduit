using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class ConduitBackgroundDispatcherShould
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task RunItemsStillQueuedWhenStopBegins()
    {
        // Arrange -- item one holds the reader so item two is still QUEUED when StopAsync starts.
        var dispatcher = new ConduitBackgroundDispatcher(NullLogger<ConduitBackgroundDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRan = false;
        dispatcher.Enqueue(async _ =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
        });
        dispatcher.Enqueue(async token =>
        {
            // As a real sender does: honour the token it is handed, so a cancelled one aborts the send.
            await Task.Delay(1, token);
            secondRan = true;
        });
        await firstStarted.Task.WaitAsync(Generous);

        // Act
        var stopping = dispatcher.StopAsync(CancellationToken.None);
        releaseFirst.SetResult();
        await stopping.WaitAsync(Generous);

        // Assert
        secondRan.Should().BeTrue("deferred:needs-graceful-drain: a queued 'your code was requested' SMS must be sent, not abandoned");
    }

    [TestMethod]
    public async Task HandDrainedItemsATokenThatIsNotCancelled()
    {
        // Arrange
        var dispatcher = new ConduitBackgroundDispatcher(NullLogger<ConduitBackgroundDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? secondSawCancelled = null;
        dispatcher.Enqueue(async _ =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
        });
        dispatcher.Enqueue(token =>
        {
            secondSawCancelled = token.IsCancellationRequested;
            return Task.CompletedTask;
        });
        await firstStarted.Task.WaitAsync(Generous);

        // Act
        var stopping = dispatcher.StopAsync(CancellationToken.None);
        releaseFirst.SetResult();
        await stopping.WaitAsync(Generous);

        // Assert -- a drained item handed a cancelled token would abort its send at once: "drained" in name only.
        secondSawCancelled.Should().BeFalse();
    }

    [TestMethod]
    public async Task AbandonWhatIsLeftAtTheHostsDeadline_AndLogTheCount()
    {
        // Arrange -- item one never finishes on its own; it honours only its token.
        var logger = new RecordingLogger<ConduitBackgroundDispatcher>();
        var dispatcher = new ConduitBackgroundDispatcher(logger);
        await dispatcher.StartAsync(CancellationToken.None);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRan = false;
        dispatcher.Enqueue(async token =>
        {
            firstStarted.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        dispatcher.Enqueue(_ =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });
        await firstStarted.Task.WaitAsync(Generous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Act
        await dispatcher.StopAsync(deadline.Token).WaitAsync(Generous);

        // Assert
        secondRan.Should().BeFalse();
        logger.Entries.Should().Contain(entry => entry.EventId.Id == 303 && entry.Level == LogLevel.Warning && entry.Message.Contains("with 1 fire-and-forget"),
            "the loss is no longer silent: one queued item was abandoned, and the log says how many");
    }

    [TestMethod]
    public async Task ReturnAtTheHostsDeadlineEvenWhenAnItemIgnoresItsToken()
    {
        // Arrange -- item one ignores its token entirely; the host's deadline must still bound StopAsync.
        var logger = new RecordingLogger<ConduitBackgroundDispatcher>();
        var dispatcher = new ConduitBackgroundDispatcher(logger);
        await dispatcher.StartAsync(CancellationToken.None);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Enqueue(async _ =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
        });
        dispatcher.Enqueue(_ => Task.CompletedTask);
        await firstStarted.Task.WaitAsync(Generous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Act
        var stopping = dispatcher.StopAsync(deadline.Token);
        var returnedInTime = await Task.WhenAny(stopping, Task.Delay(Generous)) == stopping;
        releaseFirst.SetResult();

        // Assert
        returnedInTime.Should().BeTrue("a wedged item must not hold the host's shutdown past its deadline");
        logger.Entries.Should().Contain(entry => entry.EventId.Id == 303 && entry.Message.Contains("with 1 fire-and-forget"),
            "the item still queued behind the wedged one is reported abandoned");
    }

    [TestMethod]
    public async Task StopAfterDispose_PastTheDeadline_WithoutThrowing()
    {
        // Arrange -- disposed, then stopped, while the reader is held by an item that ignores its token.
        var dispatcher = new ConduitBackgroundDispatcher(NullLogger<ConduitBackgroundDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Enqueue(async _ =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
        });
        await firstStarted.Task.WaitAsync(Generous);
        dispatcher.Dispose();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Act
        var act = async () => await dispatcher.StopAsync(deadline.Token).WaitAsync(Generous);

        // Assert
        await act.Should().NotThrowAsync();
        releaseFirst.SetResult();
    }

    [TestMethod]
    public async Task StopBeforeStartAndStopTwice_WithoutThrowing()
    {
        // Arrange
        var neverStarted = new ConduitBackgroundDispatcher(NullLogger<ConduitBackgroundDispatcher>.Instance);
        var started = new ConduitBackgroundDispatcher(NullLogger<ConduitBackgroundDispatcher>.Instance);
        await started.StartAsync(CancellationToken.None);

        // Act
        var act = async () =>
        {
            await neverStarted.StopAsync(CancellationToken.None);
            await started.StopAsync(CancellationToken.None);
            await started.StopAsync(CancellationToken.None);
        };

        // Assert
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task LogAndDropAnEnqueueAfterStop()
    {
        // Arrange
        var logger = new RecordingLogger<ConduitBackgroundDispatcher>();
        var dispatcher = new ConduitBackgroundDispatcher(logger);
        await dispatcher.StartAsync(CancellationToken.None);
        await dispatcher.StopAsync(CancellationToken.None);

        // Act
        var act = () => dispatcher.Enqueue(_ => Task.CompletedTask);

        // Assert
        act.Should().NotThrow();
        logger.Entries.Should().Contain(entry => entry.EventId.Id == 300);
    }
}
