using System.ComponentModel;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Runtime;

/// <summary>
/// Hosted service that executes fire-and-forget command handlers and event listeners
/// in the background. Work items are enqueued by the generated mediator and processed
/// sequentially. On shutdown every item already queued runs, with a live token, until the host's
/// shutdown deadline. At the deadline the running item's token is cancelled, <see cref="StopAsync"/> returns
/// even if that item ignores it, and the items still queued are logged (<c>ShutdownAbandoned</c>), not run.
/// Infrastructure for the generated mediator and Conduit's own packages; not intended for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ConduitBackgroundDispatcher : BackgroundService
{
    private readonly Channel<Func<CancellationToken, Task>> workChannel =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(
            new UnboundedChannelOptions { SingleReader = true });

    private readonly ILogger<ConduitBackgroundDispatcher> logger;

    // Cancelled only when the host's shutdown deadline (StopAsync's token) fires before the channel is drained.
    // NOT the base's stoppingToken: BackgroundService cancels that at the START of its StopAsync, which is what used to
    // abandon every queued item (deferred:needs-graceful-drain).
    private readonly CancellationTokenSource drainAbort = new();

    // Items written but not yet taken by the reader. StopAsync reports it at the deadline instead of draining the
    // channel to count: the reader may still be inside a wedged item then, and this is a SingleReader channel.
    private int pending;

    /// <summary>
    /// Creates a new background dispatcher.
    /// </summary>
    public ConduitBackgroundDispatcher(ILogger<ConduitBackgroundDispatcher> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Enqueues a work item for background execution. Returns immediately.
    /// The work item receives a token that stays live while shutdown drains the queue, and is cancelled only when
    /// the host's shutdown deadline passes.
    /// </summary>
    public void Enqueue(Func<CancellationToken, Task> workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        Interlocked.Increment(ref this.pending);
        if (!this.workChannel.Writer.TryWrite(workItem))
        {
            Interlocked.Decrement(ref this.pending);
            LogEnqueueFailed(this.logger, null);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, this.drainAbort.Token);
        var token = linked.Token;
        var reader = this.workChannel.Reader;
        try
        {
            // Not ReadAllAsync: it keeps yielding buffered items after its token is cancelled, which would run them
            // past the deadline with a dead token. Checking the token BEFORE each TryRead leaves the rest in the
            // channel, where StopAsync counts them. Ends normally once StopAsync completes the writer and the
            // reader has taken the last item.
            while (await reader.WaitToReadAsync(token))
            {
                while (!token.IsCancellationRequested && reader.TryRead(out var workItem))
                {
                    Interlocked.Decrement(ref this.pending);
                    try
                    {
                        await workItem(token);
                    }
#pragma warning disable CA1031
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // Shutdown deadline reached mid-item -- expected
                    }
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        LogBackgroundError(this.logger, ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Deadline reached between items; StopAsync logs what was left.
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Signal no more items will be written. TryComplete is idempotent --
        // StopAsync may be called more than once (e.g. test host teardown).
        this.workChannel.Writer.TryComplete();

        // Drain: let the reader run every item already queued, with a LIVE token, until the host's deadline. The wait
        // itself is bounded by the deadline too, so an item that ignores its token cannot hold shutdown past it.
        if (this.ExecuteTask is { IsCompleted: false } executeTask)
        {
            await executeTask.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!executeTask.IsCompleted)
            {
                // Deadline passed: stop the reader taking another item, so what is left stays queued and counted.
                // Cancelled here rather than from a registration on the host's token -- WaitAsync's own registration
                // runs first and resumes this method inline, which disposed ours before it ever fired.
                this.CancelDrain();
            }
        }

        var abandoned = Volatile.Read(ref this.pending);
        if (abandoned > 0)
        {
            LogShutdownAbandoned(this.logger, abandoned, null);
        }

        await base.StopAsync(cancellationToken);
    }

    private void CancelDrain()
    {
        try
        {
            this.drainAbort.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed before StopAsync -- the base already cancelled the reader's stopping token.
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        this.drainAbort.Dispose();
        base.Dispose();
    }

    private static readonly Action<ILogger, Exception?> LogEnqueueFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(300, "EnqueueFailed"),
            "Failed to enqueue fire-and-forget work item");

    private static readonly Action<ILogger, Exception?> LogBackgroundError =
        LoggerMessage.Define(LogLevel.Error, new EventId(301, "BackgroundError"),
            "Unhandled exception in fire-and-forget handler");

    private static readonly Action<ILogger, int, Exception?> LogShutdownAbandoned =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(303, "ShutdownAbandoned"),
            "Host shutdown deadline reached with {Count} fire-and-forget work item(s) not run");
}
