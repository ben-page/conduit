using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Runtime;

/// <summary>
/// Hosted service that executes fire-and-forget command handlers and event listeners
/// in the background. Work items are enqueued by the generated mediator and processed
/// sequentially with a linked cancellation token for graceful shutdown.
/// </summary>
public sealed class ConduitBackgroundDispatcher : BackgroundService
{
    private readonly Channel<Func<CancellationToken, Task>> workChannel =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(
            new UnboundedChannelOptions { SingleReader = true });

    private readonly ILogger<ConduitBackgroundDispatcher> logger;

    /// <summary>
    /// Creates a new background dispatcher.
    /// </summary>
    public ConduitBackgroundDispatcher(ILogger<ConduitBackgroundDispatcher> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Enqueues a work item for background execution. Returns immediately.
    /// The work item receives the shutdown cancellation token.
    /// </summary>
    public void Enqueue(Func<CancellationToken, Task> workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        if (!this.workChannel.Writer.TryWrite(workItem))
        {
            LogEnqueueFailed(this.logger, null);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var workItem in this.workChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await workItem(stoppingToken);
            }
#pragma warning disable CA1031
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — expected
            }
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogBackgroundError(this.logger, ex);
            }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Signal no more items will be written
        this.workChannel.Writer.Complete();

        // Let the base drain remaining items with the cancellation token
        await base.StopAsync(cancellationToken);
    }

    private static readonly Action<ILogger, Exception?> LogEnqueueFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(300, "EnqueueFailed"),
            "Failed to enqueue fire-and-forget work item");

    private static readonly Action<ILogger, Exception?> LogBackgroundError =
        LoggerMessage.Define(LogLevel.Error, new EventId(301, "BackgroundError"),
            "Unhandled exception in fire-and-forget handler");
}
