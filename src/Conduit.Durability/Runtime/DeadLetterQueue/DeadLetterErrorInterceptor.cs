using Conduit.Durability.DeadLetterQueue;
using Conduit.Middleware;
using Conduit.Runtime;
using Microsoft.Extensions.Logging;

namespace Conduit.Durability.Runtime.DeadLetterQueue;

/// <summary>
/// <see cref="IInboundErrorInterceptor"/> implementation that captures failed inbound messages
/// to the dead letter store. Registered by <c>AddDeadLetterQueue()</c>.
/// </summary>
internal sealed class DeadLetterErrorInterceptor : IInboundErrorInterceptor
{
    private readonly IDeadLetterStore store;
    private readonly ILogger<DeadLetterErrorInterceptor> logger;

    private static readonly Action<ILogger, string, Exception?> LogCaptureFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(310, "DlqCaptureFailed"),
            "Failed to capture dead letter for message type '{MessageType}'");

    public DeadLetterErrorInterceptor(
        IDeadLetterStore store,
        ILogger<DeadLetterErrorInterceptor> logger)
    {
        this.store = store;
        this.logger = logger;
    }

    public async Task<bool> TryCaptureAsync(
        string messageType,
        BinaryData body,
        string sourceEndpoint,
        Exception exception,
        string? reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            var context = MessageContext.Current;
            var entry = new DeadLetterEntry
            {
                Id = Guid.NewGuid(),
                MessageType = messageType,
                Body = body,
                SourceEndpoint = sourceEndpoint,
                ExceptionType = exception.GetType().FullName,
                ExceptionMessage = exception.Message,
                Reference = reference,
                MessageId = context?.MessageId,
                CausationId = context?.CausationId,
                ConversationId = context?.ConversationId,
                AttemptCount = 1,
                DeadLetteredAt = DateTimeOffset.UtcNow,
            };

            await this.store.WriteAsync(entry, cancellationToken);
            ConduitTelemetry.RecordDeadLettered(messageType, sourceEndpoint);
            MessageContext.Observe(new DeadLettered(exception.Message, sourceEndpoint));
            return true;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCaptureFailed(this.logger, messageType, ex);
            return false;
        }
    }
}
