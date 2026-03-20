using System.Text.Json;
using Conduit.Configuration;
using Conduit.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Runtime;

/// <summary>
/// Abstract base class for broker transports (Azure Service Bus, RabbitMQ, etc.).
/// Owns the <see cref="IHostedService"/> lifecycle, inbound dispatch orchestration,
/// and outbound send/receive. Transports implement abstract methods for
/// transport-specific primitives (connect, publish, ack/nack, etc.).
/// </summary>
/// <typeparam name="TMessage">The native inbound message type from the broker SDK.</typeparam>
public abstract class BrokerTransportBase<TMessage> : IConduitTransport, IHostedService, IAsyncDisposable
{
    // Shared logging delegates for inbound dispatch
    private static readonly Action<ILogger, string, Exception?> LogReceivedMessage =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(500, "ReceivedMessage"),
            "Received message '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogMessageCompleted =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(501, "MessageCompleted"),
            "Completed message '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogMessageAbandoned =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(502, "MessageAbandoned"),
            "Abandoned message '{Type}' — broker will redeliver");

    private static readonly Action<ILogger, string, Exception?> LogMessageProcessingError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(503, "MessageProcessingError"),
            "Error processing message '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogReplyError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(504, "ReplyError"),
            "Error sending error reply for '{Type}'");

    private static readonly Action<ILogger, Exception?> LogSkippingNoType =
        LoggerMessage.Define(LogLevel.Warning, new EventId(505, "SkippingNoType"),
            "Received message with no type discriminator — acknowledging and skipping");

    /// <summary>
    /// Creates a new broker transport base.
    /// </summary>
    /// <param name="conduitOptions">The Conduit configuration options.</param>
    /// <param name="serviceProvider">The application service provider.</param>
    /// <param name="logger">Logger for transport diagnostics.</param>
    protected BrokerTransportBase(
        ConduitOptions conduitOptions,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(conduitOptions);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.ConduitOptions = conduitOptions;
        this.ServiceProvider = serviceProvider;
        this.Logger = logger;
    }

    /// <summary>The Conduit configuration options.</summary>
    protected ConduitOptions ConduitOptions { get; }

    /// <summary>The application service provider.</summary>
    protected IServiceProvider ServiceProvider { get; }

    /// <summary>Logger for transport diagnostics.</summary>
    protected ILogger Logger { get; }

    // -----------------------------------------------------------------------
    // IHostedService — sealed template methods
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var typeRegistry = this.ServiceProvider.GetRequiredService<ConduitTypeRegistry>();

        var eventTypeNames = typeRegistry.EventTypeNames;
        var commandQueryTypeNames = typeRegistry.CommandTypeNames
            .Concat(typeRegistry.QueryTypeNames).ToList();

        await this.ConnectAsync(cancellationToken);
        await this.EnsureTopologyAsync(eventTypeNames, commandQueryTypeNames, cancellationToken);
        await this.StartConsumersAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await this.StopConsumersAsync(cancellationToken);
    }

    // -----------------------------------------------------------------------
    // IConduitTransport — sealed template methods
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!this.IsStarted)
        {
            throw new InvalidOperationException(
                "Transport has not been started. Ensure the host is running before sending messages.");
        }

        var body = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload, ConduitRuntime.JsonOptions));

        var replyData = await this.PublishRequestAsync(envelope, body, cancellationToken);

        var result = JsonSerializer.Deserialize<DispatchResult<JsonElement?>>(
            replyData, ConduitRuntime.JsonOptions);

        return ConduitRuntime.UnwrapReply(result);
    }

    /// <inheritdoc />
    public async Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!this.IsStarted)
        {
            throw new InvalidOperationException(
                "Transport has not been started. Ensure the host is running before publishing events.");
        }

        var body = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload, ConduitRuntime.JsonOptions));

        await this.PublishEventAsync(envelope, body, cancellationToken);
    }

    // -----------------------------------------------------------------------
    // Inbound dispatch — sealed template method
    // -----------------------------------------------------------------------

    /// <summary>
    /// Processes an inbound message: extracts context, creates DI scope, resolves mediator,
    /// dispatches, handles errors, and acknowledges/abandons the message.
    /// Called by the transport's native message handler.
    /// </summary>
    protected async Task ProcessInboundMessageAsync(TMessage message)
    {
        var messageType = this.GetMessageType(message);
        if (string.IsNullOrEmpty(messageType))
        {
            LogSkippingNoType(this.Logger, null);
            await this.AcknowledgeAsync(message);
            return;
        }

        LogReceivedMessage(this.Logger, messageType, null);

        var previousContext = MessageContext.Current;
        try
        {
            var (messageId, causationId, conversationId) = this.ExtractContext(message);
            MessageContext.StartDispatchFromTransport(messageId, causationId, conversationId);

            await using var scope = this.ServiceProvider.CreateAsyncScope();
            var wireMediator = scope.ServiceProvider.GetRequiredService<IWireMediator>();

            var body = this.GetMessageBody(message);
            var payload = JsonSerializer.Deserialize<JsonElement>(body, ConduitRuntime.JsonOptions);
            var envelope = new DispatchEnvelope(messageType, payload);

            if (this.IsRequest(message))
            {
                // Request/response: dispatch, send reply, complete.
                // On any failure, send an error reply and complete to prevent poison loops.
                try
                {
                    var result = await wireMediator.HandleWireRequestAsync(
                        envelope, CancellationToken.None);

                    var replyData = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
                        result, ConduitRuntime.JsonOptions));

                    await this.SendReplyAsync(message, replyData);
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogMessageProcessingError(this.Logger, messageType, ex);

                    // Best-effort: send an error reply so the caller doesn't hang
                    try
                    {
                        var errorResult = DispatchResult<object>.Fail("InternalError", ex.Message);
                        var errorReplyData = new BinaryData(JsonSerializer.SerializeToUtf8Bytes(
                            errorResult, ConduitRuntime.JsonOptions));

                        await this.SendReplyAsync(message, errorReplyData);
                    }
#pragma warning disable CA1031
                    catch (Exception replyEx)
#pragma warning restore CA1031
                    {
                        LogReplyError(this.Logger, messageType, replyEx);
                    }
                }

                await this.AcknowledgeAsync(message);
                LogMessageCompleted(this.Logger, messageType, null);
            }
            else
            {
                // Event: complete on success, abandon on EventListenerException
                try
                {
                    await wireMediator.HandleWireEventAsync(envelope, CancellationToken.None);
                    await this.AcknowledgeAsync(message);
                    LogMessageCompleted(this.Logger, messageType, null);
                }
                catch (EventListenerException ex)
                {
                    LogMessageAbandoned(this.Logger, messageType, ex);
                    await this.AbandonAsync(message);
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Outer catch: fires only if context extraction, scope creation,
            // or deserialization fails before reaching the request/event branch.
            LogMessageProcessingError(this.Logger, messageType, ex);
            await this.AcknowledgeAsync(message);
        }
        finally
        {
            MessageContext.RestoreContext(previousContext);
        }
    }

    // -----------------------------------------------------------------------
    // Abstract methods — lifecycle
    // -----------------------------------------------------------------------

    /// <summary>Opens broker connections and channels.</summary>
    protected abstract Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Creates exchanges/topics, queues, subscriptions, and bindings.</summary>
    protected abstract Task EnsureTopologyAsync(
        IReadOnlyList<string> eventTypeNames,
        IReadOnlyList<string> commandQueryTypeNames,
        CancellationToken cancellationToken);

    /// <summary>Starts message receivers/processors.</summary>
    protected abstract Task StartConsumersAsync(CancellationToken cancellationToken);

    /// <summary>Stops message receivers/processors and drains pending outbound requests.</summary>
    protected abstract Task StopConsumersAsync(CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    // -----------------------------------------------------------------------
    // Abstract methods — inbound dispatch
    // -----------------------------------------------------------------------

    /// <summary>Extracts the type discriminator from native message properties.</summary>
    protected abstract string? GetMessageType(TMessage message);

    /// <summary>Extracts the message body.</summary>
    protected abstract BinaryData GetMessageBody(TMessage message);

    /// <summary>Determines if the message expects a reply (command/query vs event).</summary>
    protected abstract bool IsRequest(TMessage message);

    /// <summary>Extracts MessageContext fields from native message properties/headers.</summary>
    protected abstract (string MessageId, string? CausationId, string ConversationId) ExtractContext(TMessage message);

    /// <summary>Sends a pre-serialized reply to the original sender using native reply mechanism.</summary>
    protected abstract Task SendReplyAsync(TMessage message, BinaryData replyBody);

    /// <summary>Marks the message as successfully processed.</summary>
    protected abstract Task AcknowledgeAsync(TMessage message);

    /// <summary>Marks the message for redelivery.</summary>
    protected abstract Task AbandonAsync(TMessage message);

    // -----------------------------------------------------------------------
    // Abstract methods — outbound
    // -----------------------------------------------------------------------

    /// <summary>Whether the transport has been started (connections open).</summary>
    protected abstract bool IsStarted { get; }

    /// <summary>Publishes a request and returns the raw reply payload.</summary>
    protected abstract Task<BinaryData> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        CancellationToken cancellationToken);

    /// <summary>Publishes an event (fire-and-forget).</summary>
    protected abstract Task PublishEventAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        CancellationToken cancellationToken);
}
