using System.Globalization;
using System.Text;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Logging;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace Conduit.Transport.AmazonSqs;

/// <summary>
/// Amazon SQS (+SNS) transport extending <see cref="BrokerTransportBase{TMessage}"/>.
/// Inbound dispatch, lifecycle, and outbound serialization are handled by the base class.
/// This class implements the SQS/SNS-specific primitives: SNS publishing, SQS long-polling
/// consumers, reply queue correlation, and topology creation.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1062:Validate arguments of public methods",
    Justification = "Protected override methods are called by the base class which validates parameters.")]
public sealed class AmazonSqsTransport : BrokerTransportBase<AmazonSqsInboundMessage>
{
    private readonly AmazonSqsOptions options;

    // The app and pool queue pollers stop first, on stop; the reply poller keeps running through the
    // drain, since a handler being drained may await a reply, and stops with the consumers.
    private readonly CancellationTokenSource inboundPolling = new();
    private readonly CancellationTokenSource replyPolling = new();
    private readonly List<Task> inboundPollers = [];
    private readonly List<SemaphoreSlim> pollerSlots = [];
    private Task? replyPoller;
    private bool replyQueueDeleted;
    private bool disposed;

    private AmazonSimpleNotificationServiceClient? snsClient;
    private AmazonSQSClient? sqsClient;
    private string? topicArn;
    private string? appQueueAddress;
    private string? replyQueueAddress;
    private Dictionary<string, string> poolQueueAddresses = new(StringComparer.Ordinal);

    // Transport-specific logging delegates
    private static readonly Action<ILogger, string, Exception?> LogSendingRequest =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(100, "SendingRequest"),
            "Sending request '{Type}' to SNS topic");

    private static readonly Action<ILogger, string, Exception?> LogPublishingEvent =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(101, "PublishingEvent"),
            "Publishing event '{Type}' to SNS topic");

    private static readonly Action<ILogger, int, Exception?> LogStartingConsumers =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(106, "StartingConsumers"),
            "Starting {Count} SQS polling consumer(s)");

    private static readonly Action<ILogger, Exception?> LogTransportStopping =
        LoggerMessage.Define(LogLevel.Information, new EventId(107, "TransportStopping"),
            "Stopping Amazon SQS transport");

    private static readonly Action<ILogger, string, Exception?> LogPollingError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(109, "PollingError"),
            "Error polling SQS queue '{QueueAddress}'");

    private static readonly Action<ILogger, string, string, Exception?> LogInboundMessageFailed =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(110, "InboundMessageFailed"),
            "Processing SQS message '{SqsMessageId}' from queue '{QueueAddress}' failed outside its dispatch; " +
            "SQS delivers it again once its visibility timeout lapses");

    private static readonly Action<ILogger, string, Exception?> LogLateReply =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(111, "LateReply"),
            "Deleted a reply nobody awaits (correlation id '{CorrelationId}'): its request timed out or was cancelled");

    private static readonly Action<ILogger, string, Exception?> LogReplyQueueNotDeleted =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(112, "ReplyQueueNotDeleted"),
            "Could not delete reply queue '{QueueAddress}'; it stays behind, empty");

    // How long disposal tries to delete a reply queue that stop did not
    private static readonly TimeSpan ReplyQueueDeleteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Creates a new Amazon SQS (+SNS) transport.
    /// </summary>
    public AmazonSqsTransport(
        AmazonSqsOptions options,
        ConduitOptions conduitOptions,
        IServiceProvider serviceProvider,
        ILogger<AmazonSqsTransport> logger)
        : base(options, conduitOptions, serviceProvider, logger)
    {
        this.options = options;

        this.ValidateOptions();
    }

    /// <inheritdoc/>
    protected override string TransportName => "sqs";

    // This instance's reply queue, under an id of its own: each instance polls its own reply queue, so a
    // reply reaches the instance that sent the request
    private string ReplyQueueName =>
        field ??= AmazonSqsTopologyManager.ReplyQueueName(this.ConduitOptions, Guid.NewGuid().ToString("N"));

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken cancellationToken)
    {
        (this.snsClient, this.sqsClient) = AmazonSqsClientFactory.Create(this.options);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task EnsureTopologyAsync(
        IReadOnlyList<string> eventTypeNames,
        IReadOnlyList<string> commandQueryTypeNames,
        CancellationToken cancellationToken)
    {
        var topologyManager = new AmazonSqsTopologyManager(
            this.snsClient!, this.sqsClient!, this.options, this.ConduitOptions);

        try
        {
            await topologyManager.EnsureTopologyAsync(
                this.ReplyQueueName, eventTypeNames, commandQueryTypeNames, cancellationToken);
        }
        finally
        {
            this.KeepReplyQueueAddress(topologyManager);
        }

        this.topicArn = topologyManager.TopicArn;
        this.appQueueAddress = topologyManager.AppQueueAddress;
        this.poolQueueAddresses = new Dictionary<string, string>(
            topologyManager.PoolQueueAddresses, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    protected override async Task EnsureOutboundTopologyAsync(CancellationToken cancellationToken)
    {
        var topologyManager = new AmazonSqsTopologyManager(
            this.snsClient!, this.sqsClient!, this.options, this.ConduitOptions);

        try
        {
            await topologyManager.EnsureOutboundTopologyAsync(this.ReplyQueueName, cancellationToken);
        }
        finally
        {
            this.KeepReplyQueueAddress(topologyManager);
        }

        this.topicArn = topologyManager.TopicArn;

        // Start reply queue consumer for request/response
        this.replyPoller = this.PollReplyQueueAsync(this.replyPolling.Token);
    }

    // Kept even when a later step of the start failed, so disposal deletes the reply queue the start created:
    // a host that fails to start on every restart would otherwise leave one behind each time
    private void KeepReplyQueueAddress(AmazonSqsTopologyManager topologyManager)
    {
        if (topologyManager.ReplyQueueAddress.Length > 0)
        {
            this.replyQueueAddress = topologyManager.ReplyQueueAddress;
        }
    }

    /// <inheritdoc />
    protected override Task StartConsumersAsync(CancellationToken cancellationToken)
    {
        var consumerCount = 1 + this.options.Pools.Count + 1; // app + pools + reply

        // Start default app queue consumer
        this.StartPoller(this.appQueueAddress!, this.options.DefaultConcurrency);

        // Start pool queue consumers
        foreach (var pool in this.options.Pools)
        {
            if (this.poolQueueAddresses.TryGetValue(pool.Name, out var poolQueueAddress))
            {
                this.StartPoller(poolQueueAddress, pool.Concurrency);
            }
        }

        // Start reply queue consumer
        this.replyPoller = this.PollReplyQueueAsync(this.replyPolling.Token);

        LogStartingConsumers(this.Logger, consumerCount, null);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task StopReceivingAsync(CancellationToken cancellationToken)
    {
        LogTransportStopping(this.Logger, null);

        // Stop polling the app and pool queues. A handler already started keeps its slot and the
        // client it deletes its message with; a message received but not yet started is made visible
        // again at once, for another instance to take.
        await this.inboundPolling.CancelAsync();
        await Task.WhenAll(this.inboundPollers)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <inheritdoc />
    protected override async Task StopConsumersAsync(CancellationToken cancellationToken)
    {
        // The drain is over: stop polling for replies (the base class fails any request still waiting)
        await this.replyPolling.CancelAsync();
        if (this.replyPoller is not null)
        {
            await this.replyPoller
                .WaitAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // Nothing reads the reply queue now. Best effort within the shutdown deadline: DisposeAsync
        // tries again, and logs a failure.
        _ = await this.DeleteReplyQueueAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        // The keyed registration and both forwarders resolve this instance, and the container
        // disposes what each factory returns
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        // Stopped already, unless the host never started or stopped it
        await this.inboundPolling.CancelAsync();
        await this.replyPolling.CancelAsync();
        this.inboundPolling.Dispose();
        this.replyPolling.Dispose();

        // Deleted on stop, unless stop never ran or ran out of time
        using (var timeout = new CancellationTokenSource(ReplyQueueDeleteTimeout))
        {
            if (await this.DeleteReplyQueueAsync(timeout.Token) is { } failure)
            {
                LogReplyQueueNotDeleted(this.Logger, this.replyQueueAddress!, failure);
            }
        }

        foreach (var slots in this.pollerSlots)
        {
            slots.Dispose();
        }

        this.snsClient?.Dispose();
        this.sqsClient?.Dispose();
    }

    // -----------------------------------------------------------------------
    // Inbound dispatch primitives
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override string? GetMessageType(AmazonSqsInboundMessage message)
    {
        // AWSSDK v4 leaves an absent collection null where v3 gave an empty one, so every read of a message's
        // MessageAttributes, or a response's Messages, here tolerates null.
        if (message.Message.MessageAttributes?.TryGetValue("Type", out var typeAttribute) == true)
        {
            return typeAttribute.StringValue;
        }

        return null;
    }

    /// <inheritdoc />
    protected override BinaryData GetMessageBody(AmazonSqsInboundMessage message) =>
        new(message.Message.Body);

    /// <inheritdoc />
    protected override bool IsRequest(AmazonSqsInboundMessage message) =>
        message.Message.MessageAttributes?.ContainsKey("ReplyQueueAddress") == true;

    /// <inheritdoc />
    protected override (string MessageId, string? CausationId, string ConversationId) ExtractContext(
        AmazonSqsInboundMessage message)
    {
        var messageId = GetAttributeValue(message, "MessageId")
            ?? Guid.NewGuid().ToString("N");
        var causationId = GetAttributeValue(message, "CausationId");
        var conversationId = GetAttributeValue(message, "ConversationId")
            ?? messageId;

        return (messageId, causationId, conversationId);
    }

    /// <inheritdoc />
    protected override async Task SendReplyAsync(
        AmazonSqsInboundMessage message, BinaryData replyBody)
    {
        var replyQueueAddress = GetAttributeValue(message, "ReplyQueueAddress");
        var correlationId = GetAttributeValue(message, "CorrelationId");

        if (string.IsNullOrEmpty(replyQueueAddress))
        {
            return;
        }

        // Send reply directly to the caller's reply SQS queue -- bypasses SNS
        var sendRequest = new SendMessageRequest
        {
            QueueUrl = replyQueueAddress,
            MessageBody = replyBody.ToString(),
            MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>(),
        };

        if (!string.IsNullOrEmpty(correlationId))
        {
            sendRequest.MessageAttributes["CorrelationId"] = new SqsMessageAttributeValue
            {
                DataType = "String",
                StringValue = correlationId,
            };
        }

        await this.sqsClient!.SendMessageAsync(sendRequest);
    }

    /// <inheritdoc />
    protected override async Task AcknowledgeAsync(AmazonSqsInboundMessage message)
    {
        await this.sqsClient!.DeleteMessageAsync(
            message.SourceQueue, message.Message.ReceiptHandle);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Leaves the message invisible until its visibility timeout lapses: SQS then delivers it again,
    /// counting the delivery, and once it has delivered it <see cref="AmazonSqsOptions.MaxReceiveCount"/>
    /// times the queue's redrive policy moves it to the queue's dead-letter queue. So a failure is retried
    /// a visibility timeout apart, never in a loop.
    /// </remarks>
    protected override Task AbandonAsync(AmazonSqsInboundMessage message) => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// Makes the message visible again at once, rather than when its visibility timeout lapses: nothing
    /// about it failed, and another instance can take it now. SQS counts the delivery all the same.
    /// </remarks>
    protected override Task RequeueAsync(AmazonSqsInboundMessage message) =>
        this.MakeVisibleAsync(message.SourceQueue, [message.Message]);

    // -----------------------------------------------------------------------
    // Outbound primitives
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override bool IsStarted =>
        this.snsClient is not null && this.topicArn is not null;

    /// <inheritdoc />
    protected override async Task<ReadOnlyMemory<byte>> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        IReadOnlyDictionary<string, string> headers,
        string correlationId,
        CancellationToken cancellationToken)
    {
        LogSendingRequest(this.Logger, envelope.Type, null);

        var messageAttributes = BuildOutboundAttributes(envelope.Type, headers);
        messageAttributes["ReplyQueueAddress"] = new SqsMessageAttributeValue
        {
            DataType = "String",
            StringValue = this.replyQueueAddress,
        };
        messageAttributes["CorrelationId"] = new SqsMessageAttributeValue
        {
            DataType = "String",
            StringValue = correlationId,
        };

        await this.snsClient!.PublishAsync(
            new PublishRequest
            {
                TopicArn = this.topicArn,
                Message = body.ToString(),
                MessageAttributes = ConvertToSnsAttributes(messageAttributes),
            },
            cancellationToken);

        return await this.AwaitReplyAsync(correlationId, cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task PublishEventAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        LogPublishingEvent(this.Logger, envelope.Type, null);

        var messageAttributes = BuildOutboundAttributes(envelope.Type, headers);

        await this.snsClient!.PublishAsync(
            new PublishRequest
            {
                TopicArn = this.topicArn,
                Message = body.ToString(),
                MessageAttributes = ConvertToSnsAttributes(messageAttributes),
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public override async Task RedeliverAsync(
        DispatchEnvelope envelope,
        RedeliveryIds? ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.sqsClient is null || this.appQueueAddress is null)
        {
            throw new InvalidOperationException(
                "Transport has not been started. Ensure the host is running before redelivering messages.");
        }

        // Replay deliberately bypasses the base class's BuildOutboundHeaders: its metadata is the
        // original ids only (BuildRedeliveryHeaders), with no conduit-source-app stamp, which would
        // make the inbound self-published filter drop a replayed event the moment it arrived. Do
        // not route replay through the base send path.
        var body = new BinaryData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload, ConduitRuntime.JsonOptions));

        // Send directly to the app SQS queue — bypasses SNS topic routing
        var sendRequest = new SendMessageRequest
        {
            QueueUrl = this.appQueueAddress,
            MessageBody = body.ToString(),
            MessageAttributes = BuildOutboundAttributes(envelope.Type, BuildRedeliveryHeaders(ids)),
        };

        await this.sqsClient.SendMessageAsync(sendRequest, cancellationToken);
    }

    // -----------------------------------------------------------------------
    // Trace context
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override string? ExtractMessageHeader(AmazonSqsInboundMessage message, string key) =>
        message.Message.MessageAttributes?.TryGetValue(key, out var attr) == true
            ? attr.StringValue
            : null;

    // -----------------------------------------------------------------------
    // Private helpers -- polling loops
    // -----------------------------------------------------------------------

    private void StartPoller(string queueAddress, int maxConcurrency)
    {
        // A field, not a using in the loop: a handler releases its slot after the poller has stopped,
        // so the slots live until DisposeAsync
        var slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        this.pollerSlots.Add(slots);
        this.inboundPollers.Add(this.PollQueueAsync(queueAddress, slots, this.inboundPolling.Token));
    }

    private async Task PollQueueAsync(
        string queueAddress, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await this.sqsClient!.ReceiveMessageAsync(
                    new ReceiveMessageRequest
                    {
                        QueueUrl = queueAddress,
                        MaxNumberOfMessages = 10,
                        WaitTimeSeconds = 20, // SQS long-polling
                        MessageSystemAttributeNames = ["All"],
                        MessageAttributeNames = ["All"],
                    },
                    cancellationToken);

                var messages = response.Messages ?? [];
                for (var index = 0; index < messages.Count; index++)
                {
                    try
                    {
                        await semaphore.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Receiving stopped with the rest of the batch received but not started: hand it
                        // back visible at once, as the drain does a message delivered after it began
                        await this.MakeVisibleAsync(queueAddress, messages.Skip(index));
                        throw;
                    }

                    // Not awaited: the slot bounds concurrency, and the base class tracks the call so
                    // stop can drain it. No token: a message that holds a slot always runs, or is
                    // handed back by the base class once stop is draining.
                    var sqsMessage = messages[index];
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var inboundMessage = new AmazonSqsInboundMessage(sqsMessage, queueAddress);
                            await this.ProcessInboundMessageAsync(inboundMessage);
                        }
#pragma warning disable CA1031
                        catch (Exception exception)
#pragma warning restore CA1031
                        {
                            // What escapes the dispatch (a delete that failed) leaves the message
                            // unsettled, and nothing else observes this task
                            LogInboundMessageFailed(this.Logger, sqsMessage.MessageId, queueAddress, exception);
                        }
                        finally
                        {
                            ReleaseSlot(semaphore);
                        }
                    }, CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPollingError(this.Logger, queueAddress, ex);
                // Brief pause before retrying to avoid tight error loops
                try
                {
                    await Task.Delay(1000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static void ReleaseSlot(SemaphoreSlim semaphore)
    {
        try
        {
            semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed under a handler the shutdown deadline cut short; nothing waits for the slot now
        }
    }

    /// <summary>
    /// Makes received messages visible again at once, for another instance to take. Best effort: one it
    /// fails for reappears when its visibility timeout lapses.
    /// </summary>
    private async Task MakeVisibleAsync(string queueAddress, IEnumerable<Message> messages)
    {
        var entries = messages
            .Select((message, index) => new ChangeMessageVisibilityBatchRequestEntry
            {
                Id = index.ToString(CultureInfo.InvariantCulture),
                ReceiptHandle = message.ReceiptHandle,
                VisibilityTimeout = 0,
            })
            .ToList();

        if (entries.Count == 0 || this.sqsClient is null)
        {
            return;
        }

        try
        {
            await this.sqsClient.ChangeMessageVisibilityBatchAsync(
                new ChangeMessageVisibilityBatchRequest { QueueUrl = queueAddress, Entries = entries },
                CancellationToken.None);
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // Visible again when the visibility timeout lapses
        }
    }

    /// <summary>
    /// Deletes this instance's reply queue, once nothing reads it: SQS has no auto-delete, so every
    /// instance that stopped would leave one behind. Returns why it could not, or null.
    /// </summary>
    private async Task<Exception?> DeleteReplyQueueAsync(CancellationToken cancellationToken)
    {
        if (this.replyQueueDeleted || this.sqsClient is null || this.replyQueueAddress is null)
        {
            return null;
        }

        try
        {
            await this.sqsClient.DeleteQueueAsync(
                new DeleteQueueRequest { QueueUrl = this.replyQueueAddress }, cancellationToken);
        }
        catch (QueueDoesNotExistException)
        {
            // Already gone
        }
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return exception;
        }

        this.replyQueueDeleted = true;
        return null;
    }

    private async Task PollReplyQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await this.sqsClient!.ReceiveMessageAsync(
                    new ReceiveMessageRequest
                    {
                        QueueUrl = this.replyQueueAddress!,
                        MaxNumberOfMessages = 10,
                        WaitTimeSeconds = 20,
                        MessageSystemAttributeNames = ["All"],
                        MessageAttributeNames = ["All"],
                    },
                    cancellationToken);

                foreach (var sqsMessage in response.Messages ?? [])
                {
                    var correlationId = sqsMessage.MessageAttributes?
                        .TryGetValue("CorrelationId", out var attr) == true
                            ? attr.StringValue
                            : null;

                    // The queue is this instance's, so a reply nobody awaits is a late one to its own request
                    if (!string.IsNullOrEmpty(correlationId)
                        && !this.CompleteReply(correlationId, Encoding.UTF8.GetBytes(sqsMessage.Body)))
                    {
                        LogLateReply(this.Logger, correlationId, null);
                    }

                    // Always delete reply messages after processing
                    await this.sqsClient.DeleteMessageAsync(
                        this.replyQueueAddress!, sqsMessage.ReceiptHandle, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPollingError(this.Logger, this.replyQueueAddress ?? "reply-queue", ex);
                try
                {
                    await Task.Delay(1000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Private helpers -- attributes
    // -----------------------------------------------------------------------

    /// <summary>
    /// Maps the outbound headers assembled by the base class onto SNS/SQS message attributes.
    /// </summary>
    /// <remarks>
    /// The correlation headers are renamed to the historical on-wire names that
    /// <see cref="ExtractContext"/> reads; every other header travels under its canonical key.
    /// They are renamed rather than duplicated because SNS and SQS both cap a message at ten
    /// message attributes, and a request publish already carries nine at worst: Type, the three
    /// correlation attributes, traceparent, tracestate (written whenever the current activity
    /// carries one), conduit-source-app, ReplyQueueAddress, and CorrelationId. Duplicating the
    /// three correlation headers under both their canonical and historical names would put a
    /// request publish at twelve — over the ceiling.
    /// </remarks>
    private static Dictionary<string, SqsMessageAttributeValue> BuildOutboundAttributes(
        string type,
        IReadOnlyDictionary<string, string> headers)
    {
        var attributes = new Dictionary<string, SqsMessageAttributeValue>(StringComparer.Ordinal)
        {
            ["Type"] = new SqsMessageAttributeValue
            {
                DataType = "String",
                StringValue = type,
            },
        };

        foreach (var (key, value) in headers)
        {
            attributes[ToAttributeName(key)] = new SqsMessageAttributeValue
            {
                DataType = "String",
                StringValue = value,
            };
        }

        return attributes;
    }

    /// <summary>
    /// The on-wire attribute name for an outbound header key. The correlation headers keep the
    /// names this transport has always written; everything else keeps its canonical key.
    /// </summary>
    private static string ToAttributeName(string headerKey) => headerKey switch
    {
        ConduitHeaders.MessageId => "MessageId",
        ConduitHeaders.CausationId => "CausationId",
        ConduitHeaders.ConversationId => "ConversationId",
        _ => headerKey,
    };

    /// <summary>
    /// Converts SQS-style MessageAttributeValue dict to SNS-style for PublishRequest.
    /// SNS and SQS both use <c>MessageAttributeValue</c> but from different namespaces.
    /// </summary>
    private static Dictionary<string, SnsMessageAttributeValue> ConvertToSnsAttributes(
        Dictionary<string, SqsMessageAttributeValue> sqsAttributes)
    {
        var snsAttributes = new Dictionary<string, SnsMessageAttributeValue>();

        foreach (var (key, value) in sqsAttributes)
        {
            snsAttributes[key] = new SnsMessageAttributeValue
            {
                DataType = value.DataType,
                StringValue = value.StringValue,
            };
        }

        return snsAttributes;
    }

    private static string? GetAttributeValue(
        AmazonSqsInboundMessage message, string attributeName)
    {
        if (message.Message.MessageAttributes?.TryGetValue(attributeName, out var attribute) == true)
        {
            return attribute.StringValue;
        }

        return null;
    }

    private void ValidateOptions()
    {
        if (!this.options.RawMessageDelivery)
        {
            throw new InvalidOperationException(
                "RawMessageDelivery must be enabled (true) for the Amazon SQS transport. " +
                "SNS filter policies on message attributes require raw message delivery. " +
                "Without it, SNS wraps the message in a JSON envelope and attributes are " +
                "embedded in the body rather than exposed as SQS message attributes.");
        }

        if (this.options.MaxReceiveCount is < 1 or > 1000)
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "MaxReceiveCount ({0}) must be between 1 and 1000, the range SQS accepts for the " +
                    "maxReceiveCount of a redrive policy.",
                    this.options.MaxReceiveCount));
        }

        if (this.options.VisibilityTimeoutSeconds < this.options.ResponseTimeout.TotalSeconds)
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "VisibilityTimeoutSeconds ({0}) must be " +
                    "greater than or equal to ResponseTimeout ({1}s). " +
                    "If a handler takes longer than the visibility timeout, the message becomes visible " +
                    "again and another consumer could pick it up, causing duplicate processing.",
                    this.options.VisibilityTimeoutSeconds,
                    this.options.ResponseTimeout.TotalSeconds));
        }
    }
}
