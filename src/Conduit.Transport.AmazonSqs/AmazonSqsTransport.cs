using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Amazon.Runtime;
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
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> pendingReplies = new();
    private readonly List<Task> pollingTasks = [];

    [ThreadStatic]
    private static Dictionary<string, string>? pendingTraceHeaders;

    private AmazonSimpleNotificationServiceClient? snsClient;
    private AmazonSQSClient? sqsClient;
    private string? topicArn;
    private string? appQueueAddress;
    private string? replyQueueAddress;
    private Dictionary<string, string> poolQueueAddresses = new(StringComparer.Ordinal);
    private CancellationTokenSource? pollingCts;

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

    private static readonly Action<ILogger, string, Exception?> LogReplyTimeout =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(108, "ReplyTimeout"),
            "Timed out waiting for reply with correlation '{CorrelationId}'");

    private static readonly Action<ILogger, string, Exception?> LogPollingError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(109, "PollingError"),
            "Error polling SQS queue '{QueueAddress}'");

    /// <summary>
    /// Creates a new Amazon SQS (+SNS) transport.
    /// </summary>
    public AmazonSqsTransport(
        AmazonSqsOptions options,
        ConduitOptions conduitOptions,
        IServiceProvider serviceProvider,
        ILogger<AmazonSqsTransport> logger)
        : base(conduitOptions, serviceProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;

        this.ValidateOptions();
    }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (this.options.ServiceUri is not null)
        {
            // LocalStack or custom endpoint — use explicit credentials
            // (LocalStack doesn't validate them but the SDK requires a credential source)
            var serviceUrl = this.options.ServiceUri.ToString();
            var credentials = this.options.Credentials
                ?? new BasicAWSCredentials("test", "test");

            var snsConfig = new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = serviceUrl,
            };
            this.snsClient = new AmazonSimpleNotificationServiceClient(credentials, snsConfig);

            var sqsConfig = new AmazonSQSConfig
            {
                ServiceURL = serviceUrl,
            };
            this.sqsClient = new AmazonSQSClient(credentials, sqsConfig);
        }
        else if (this.options.Region is not null)
        {
            this.snsClient = new AmazonSimpleNotificationServiceClient(this.options.Region);
            this.sqsClient = new AmazonSQSClient(this.options.Region);
        }
        else
        {
            // Default credential chain + default region
            this.snsClient = new AmazonSimpleNotificationServiceClient();
            this.sqsClient = new AmazonSQSClient();
        }

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

        await topologyManager.EnsureTopologyAsync(
            eventTypeNames, commandQueryTypeNames, cancellationToken);

        this.topicArn = topologyManager.TopicArn;
        this.appQueueAddress = topologyManager.AppQueueAddress;
        this.replyQueueAddress = topologyManager.ReplyQueueAddress;
        this.poolQueueAddresses = new Dictionary<string, string>(
            topologyManager.PoolQueueAddresses, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    protected override async Task EnsureOutboundTopologyAsync(CancellationToken cancellationToken)
    {
        var topologyManager = new AmazonSqsTopologyManager(
            this.snsClient!, this.sqsClient!, this.options, this.ConduitOptions);

        await topologyManager.EnsureOutboundTopologyAsync(cancellationToken);

        this.topicArn = topologyManager.TopicArn;
        this.replyQueueAddress = topologyManager.ReplyQueueAddress;

        // Start reply queue consumer for request/response
        this.pollingCts = new CancellationTokenSource();
        this.pollingTasks.Add(this.PollReplyQueueAsync(this.pollingCts.Token));
    }

    /// <inheritdoc />
    protected override Task StartConsumersAsync(CancellationToken cancellationToken)
    {
        this.pollingCts = new CancellationTokenSource();
        var consumerCount = 1 + this.options.Pools.Count + 1; // app + pools + reply

        // Start default app queue consumer
        this.pollingTasks.Add(this.PollQueueAsync(
            this.appQueueAddress!, this.options.DefaultMaxConcurrency, this.pollingCts.Token));

        // Start pool queue consumers
        foreach (var pool in this.options.Pools)
        {
            if (this.poolQueueAddresses.TryGetValue(pool.Name, out var poolQueueAddress))
            {
                this.pollingTasks.Add(this.PollQueueAsync(
                    poolQueueAddress, pool.MaxConcurrency, this.pollingCts.Token));
            }
        }

        // Start reply queue consumer
        this.pollingTasks.Add(this.PollReplyQueueAsync(this.pollingCts.Token));

        LogStartingConsumers(this.Logger, consumerCount, null);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task StopConsumersAsync(CancellationToken cancellationToken)
    {
        LogTransportStopping(this.Logger, null);

        // Signal cancellation to all polling loops
        if (this.pollingCts is not null)
        {
            await this.pollingCts.CancelAsync();
        }

        // Wait for in-flight processing to complete (bounded by ResponseTimeout)
        if (this.pollingTasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(this.pollingTasks)
                    .WaitAsync(this.options.ResponseTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                // Drain timeout exceeded -- proceed with shutdown
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }

        // Cancel pending outbound reply waiters
        foreach (var pending in this.pendingReplies.Values)
        {
            pending.TrySetCanceled(cancellationToken);
        }

        this.pendingReplies.Clear();
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        this.pollingCts?.Dispose();
        this.pollingTasks.Clear();

        this.snsClient?.Dispose();
        this.sqsClient?.Dispose();

        return ValueTask.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Inbound dispatch primitives
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override string? GetMessageType(AmazonSqsInboundMessage message)
    {
        if (message.Message.MessageAttributes.TryGetValue("Type", out var typeAttribute))
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
        message.Message.MessageAttributes.ContainsKey("ReplyQueueAddress");

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
    protected override Task AbandonAsync(AmazonSqsInboundMessage message)
    {
        // Do nothing -- let visibility timeout expire for redelivery.
        // SQS native maxReceiveCount + redrive policy handles dead-lettering.
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Outbound primitives
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override bool IsStarted =>
        this.snsClient is not null && this.topicArn is not null;

    /// <inheritdoc />
    protected override async Task<BinaryData> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        CancellationToken cancellationToken)
    {
        LogSendingRequest(this.Logger, envelope.Type, null);

        var correlationId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingReplies[correlationId] = tcs;

        try
        {
            var messageAttributes = BuildOutboundAttributes(envelope.Type);
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

            // Wait for reply with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(this.options.ResponseTimeout);

            using var registration = timeoutCts.Token.Register(() =>
                tcs.TrySetCanceled(timeoutCts.Token));

            var replyBytes = await tcs.Task;
            return new BinaryData(replyBytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogReplyTimeout(this.Logger, correlationId, null);
            throw new TimeoutException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Timed out waiting for reply with correlation '{0}'. " +
                    "The remote handler may have crashed or is taking too long.",
                    correlationId));
        }
        finally
        {
            this.pendingReplies.TryRemove(correlationId, out _);
        }
    }

    /// <inheritdoc />
    protected override async Task PublishEventAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        CancellationToken cancellationToken)
    {
        LogPublishingEvent(this.Logger, envelope.Type, null);

        var messageAttributes = BuildOutboundAttributes(envelope.Type);

        await this.snsClient!.PublishAsync(
            new PublishRequest
            {
                TopicArn = this.topicArn,
                Message = body.ToString(),
                MessageAttributes = ConvertToSnsAttributes(messageAttributes),
            },
            cancellationToken);
    }

    // -----------------------------------------------------------------------
    // Trace context
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override void InjectTraceHeader(string key, string value)
    {
        pendingTraceHeaders ??= new Dictionary<string, string>();
        pendingTraceHeaders[key] = value;
    }

    /// <inheritdoc />
    protected override string? ExtractTraceHeader(AmazonSqsInboundMessage message, string key) =>
        message.Message.MessageAttributes.TryGetValue(key, out var attr)
            ? attr.StringValue
            : null;

    // -----------------------------------------------------------------------
    // Private helpers -- polling loops
    // -----------------------------------------------------------------------

    private async Task PollQueueAsync(
        string queueAddress, int maxConcurrency, CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

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

                foreach (var sqsMessage in response.Messages)
                {
                    await semaphore.WaitAsync(cancellationToken);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var inboundMessage = new AmazonSqsInboundMessage(sqsMessage, queueAddress);
                            await this.ProcessInboundMessageAsync(inboundMessage);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken);
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

                foreach (var sqsMessage in response.Messages)
                {
                    var correlationId = sqsMessage.MessageAttributes
                        .TryGetValue("CorrelationId", out var attr)
                            ? attr.StringValue
                            : null;

                    if (!string.IsNullOrEmpty(correlationId) &&
                        this.pendingReplies.TryRemove(correlationId, out var tcs))
                    {
                        tcs.TrySetResult(Encoding.UTF8.GetBytes(sqsMessage.Body));
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

    private static Dictionary<string, SqsMessageAttributeValue> BuildOutboundAttributes(string type)
    {
        var attributes = new Dictionary<string, SqsMessageAttributeValue>
        {
            ["Type"] = new SqsMessageAttributeValue
            {
                DataType = "String",
                StringValue = type,
            },
        };

        var context = MessageContext.Current;
        if (context is not null)
        {
            attributes["MessageId"] = new SqsMessageAttributeValue
            {
                DataType = "String",
                StringValue = context.MessageId,
            };

            if (context.CausationId is not null)
            {
                attributes["CausationId"] = new SqsMessageAttributeValue
                {
                    DataType = "String",
                    StringValue = context.CausationId,
                };
            }

            attributes["ConversationId"] = new SqsMessageAttributeValue
            {
                DataType = "String",
                StringValue = context.ConversationId,
            };
        }

        if (pendingTraceHeaders is not null)
        {
            foreach (var (key, value) in pendingTraceHeaders)
            {
                attributes[key] = new SqsMessageAttributeValue
                {
                    DataType = "String",
                    StringValue = value,
                };
            }

            pendingTraceHeaders = null;
        }

        return attributes;
    }

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
        if (message.Message.MessageAttributes.TryGetValue(attributeName, out var attribute))
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
