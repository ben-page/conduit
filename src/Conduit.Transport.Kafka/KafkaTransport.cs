using System.Collections.Concurrent;
using System.Text;
using Conduit.Configuration;
using Conduit.Runtime;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.Kafka;

/// <summary>
/// Kafka transport extending <see cref="BrokerTransportBase{TMessage}"/>.
/// Uses a single shared topic for all messages, two consumer groups per app
/// (shared for commands/queries, per-app for events), and a per-app reply topic
/// for request/response correlation.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1062:Validate arguments of public methods",
    Justification = "Protected override methods are called by the base class which validates parameters.")]
public sealed class KafkaTransport : BrokerTransportBase<KafkaInboundMessage>
{
    private readonly KafkaOptions options;
    private readonly string instanceId = Guid.NewGuid().ToString("N")[..12];

    private IProducer<string, byte[]>? producer;
    private IConsumer<string, byte[]>? commandQueryConsumer;
    private IConsumer<string, byte[]>? eventConsumer;
    private IConsumer<string, byte[]>? replyConsumer;

    private CancellationTokenSource? consumerCts;
    private Task? commandQueryConsumerTask;
    private Task? eventConsumerTask;
    private Task? replyConsumerTask;
    private Task? cleanupTask;

    private string? sharedTopicName;
    private string? replyTopicName;
    private string? deadLetterTopicName;

    private HashSet<string> eventTypeNames = [];
    private HashSet<string> commandQueryTypeNames = [];

    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> pendingReplies = new();

    // Transport-specific logging delegates
    private static readonly Action<ILogger, string, Exception?> LogPublishingEvent =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(100, "PublishingEvent"),
            "Publishing event '{Type}' to Kafka topic");

    private static readonly Action<ILogger, string, Exception?> LogSendingRequest =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(101, "SendingRequest"),
            "Sending request '{Type}' to Kafka topic");

    private static readonly Action<ILogger, Exception?> LogStartingConsumers =
        LoggerMessage.Define(LogLevel.Information, new EventId(105, "StartingConsumers"),
            "Starting Kafka consumers");

    private static readonly Action<ILogger, Exception?> LogTransportStopping =
        LoggerMessage.Define(LogLevel.Information, new EventId(106, "TransportStopping"),
            "Stopping Kafka transport");

    private static readonly Action<ILogger, string, Exception?> LogDeadLettering =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(108, "DeadLettering"),
            "Dead-lettering message '{Type}'");

    private static readonly Action<ILogger, Exception?> LogDeadLetterError =
        LoggerMessage.Define(LogLevel.Error, new EventId(109, "DeadLetterError"),
            "Failed to publish message to dead-letter topic");

    /// <summary>
    /// Creates a new Kafka transport instance.
    /// </summary>
    public KafkaTransport(
        KafkaOptions options,
        ConduitOptions conduitOptions,
        IServiceProvider serviceProvider,
        ILogger<KafkaTransport> logger)
        : base(conduitOptions, serviceProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken cancellationToken)
    {
        var producerConfig = this.options.ProducerConfig ?? new ProducerConfig();
        producerConfig.BootstrapServers = this.options.BootstrapServers;

        this.producer = new ProducerBuilder<string, byte[]>(producerConfig).Build();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task EnsureTopologyAsync(
        IReadOnlyList<string> eventTypeNames,
        IReadOnlyList<string> commandQueryTypeNames,
        CancellationToken cancellationToken)
    {
        this.eventTypeNames = new HashSet<string>(eventTypeNames, StringComparer.Ordinal);
        this.commandQueryTypeNames = new HashSet<string>(commandQueryTypeNames, StringComparer.Ordinal);

        await this.EnsureTopicNamesAndTopologyAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task EnsureOutboundTopologyAsync(CancellationToken cancellationToken)
    {
        await this.EnsureTopicNamesAndTopologyAsync(cancellationToken);
        this.StartReplyConsumer();
    }

    private async Task EnsureTopicNamesAndTopologyAsync(CancellationToken cancellationToken)
    {
        this.sharedTopicName = TopologyNaming.CreateSharedResourceName(this.ConduitOptions, 249);
        this.replyTopicName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, "replies", 249);
        this.deadLetterTopicName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, "dead-letter", 249);

        var topologyManager = new KafkaTopologyManager(this.options, this.ConduitOptions, this.Logger);
        await topologyManager.EnsureTopologyAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override Task StartConsumersAsync(CancellationToken cancellationToken)
    {
        LogStartingConsumers(this.Logger, null);

        this.consumerCts = new CancellationTokenSource();
        var token = this.consumerCts.Token;

        // Command/query consumer — shared group (unicast).
        // Only start if this host handles command/query types. A host with no
        // command/query types in its registry must not join the shared group —
        // it would steal partitions and discard messages other hosts need.
        if (this.commandQueryTypeNames.Count > 0)
        {
            var sharedGroupName = TopologyNaming.CreateSharedResourceName(this.ConduitOptions, 249);
            this.commandQueryConsumer = this.CreateConsumer(sharedGroupName);
            this.commandQueryConsumer.Subscribe(this.sharedTopicName);
            this.commandQueryConsumerTask = Task.Run(
                () => this.ConsumeLoopAsync(this.commandQueryConsumer, isCommandQueryConsumer: true, token), token);
        }

        // Event consumer — per-app group (multicast).
        // Only start if this host handles event types.
        if (this.eventTypeNames.Count > 0)
        {
            var appGroupName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, 249);
            this.eventConsumer = this.CreateConsumer(appGroupName);
            this.eventConsumer.Subscribe(this.sharedTopicName);
            this.eventConsumerTask = Task.Run(
                () => this.ConsumeLoopAsync(this.eventConsumer, isCommandQueryConsumer: false, token), token);
        }

        this.StartReplyConsumer();

        // Start cleanup task for ephemeral topology modes
        if (this.ConduitOptions.TopologyMode != TopologyMode.Production)
        {
            this.cleanupTask = Task.Run(() => this.CleanupLoopAsync(this.consumerCts.Token), this.consumerCts.Token);
        }

        return Task.CompletedTask;
    }

    private void StartReplyConsumer()
    {
        this.consumerCts ??= new CancellationTokenSource();
        var token = this.consumerCts.Token;

        // Reply consumer — unique group per instance, manual partition assignment
        var replyGroupName = $"{TopologyNaming.CreateAppResourceName(this.ConduitOptions, "replies", 249)}-{this.instanceId}";
        this.replyConsumer = this.CreateConsumer(replyGroupName);
        this.replyConsumer.Assign([new TopicPartition(this.replyTopicName, new Partition(0))]);
        this.replyConsumerTask = Task.Run(() => this.ReplyConsumeLoopAsync(this.replyConsumer, token), token);
    }

    /// <inheritdoc />
    protected override async Task StopConsumersAsync(CancellationToken cancellationToken)
    {
        LogTransportStopping(this.Logger, null);

        // Signal cancellation to all consumer loops
        if (this.consumerCts is not null)
        {
            await this.consumerCts.CancelAsync();
        }

        // Wait for in-flight message processing (bounded by ResponseTimeout)
        var timeout = this.options.ResponseTimeout;
        var tasks = new List<Task>();
        if (this.commandQueryConsumerTask is not null)
        {
            tasks.Add(this.commandQueryConsumerTask);
        }

        if (this.eventConsumerTask is not null)
        {
            tasks.Add(this.eventConsumerTask);
        }

        if (this.replyConsumerTask is not null)
        {
            tasks.Add(this.replyConsumerTask);
        }

        if (this.cleanupTask is not null)
        {
            tasks.Add(this.cleanupTask);
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(timeout, cancellationToken));
        }

        // Close consumers gracefully to leave consumer groups (avoids rebalance timeout delay)
        this.commandQueryConsumer?.Close();
        this.eventConsumer?.Close();
        this.replyConsumer?.Close();

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
        this.consumerCts?.Dispose();
        this.commandQueryConsumer?.Dispose();
        this.eventConsumer?.Dispose();
        this.replyConsumer?.Dispose();
        this.producer?.Dispose();
        return ValueTask.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Inbound dispatch primitives
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override string? GetMessageType(KafkaInboundMessage message) =>
        GetHeaderString(message.ConsumeResult.Message.Headers, "conduit-type");

    /// <inheritdoc />
    protected override BinaryData GetMessageBody(KafkaInboundMessage message) =>
        new(message.ConsumeResult.Message.Value);

    /// <inheritdoc />
    protected override bool IsRequest(KafkaInboundMessage message) =>
        GetHeaderString(message.ConsumeResult.Message.Headers, "conduit-reply-topic") is not null;

    /// <inheritdoc />
    protected override (string MessageId, string? CausationId, string ConversationId) ExtractContext(
        KafkaInboundMessage message)
    {
        var headers = message.ConsumeResult.Message.Headers;
        var messageId = GetHeaderString(headers, "conduit-message-id") ?? Guid.NewGuid().ToString("N");
        var causationId = GetHeaderString(headers, "conduit-causation-id");
        var conversationId = GetHeaderString(headers, "conduit-conversation-id") ?? messageId;

        return (messageId, causationId, conversationId);
    }

    /// <inheritdoc />
    protected override async Task SendReplyAsync(KafkaInboundMessage message, BinaryData replyBody)
    {
        if (this.producer is null)
        {
            return;
        }

        var replyTopic = GetHeaderString(message.ConsumeResult.Message.Headers, "conduit-reply-topic");
        var correlationId = GetHeaderString(message.ConsumeResult.Message.Headers, "conduit-correlation-id");

        if (string.IsNullOrEmpty(replyTopic) || string.IsNullOrEmpty(correlationId))
        {
            return;
        }

        var replyMessage = new Message<string, byte[]>
        {
            Key = correlationId,
            Value = replyBody.ToArray(),
            Headers = new Headers
            {
                { "conduit-correlation-id", Encoding.UTF8.GetBytes(correlationId) },
            },
        };

        await this.producer.ProduceAsync(replyTopic, replyMessage);
    }

    /// <inheritdoc />
    protected override Task AcknowledgeAsync(KafkaInboundMessage message)
    {
        // Offset commit is handled by the consumer loop after ProcessInboundMessageAsync returns.
        // The consumer calls StoreOffset + Commit after each successfully processed message.
        // This is a no-op — the consumer loop commits offsets.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task AbandonAsync(KafkaInboundMessage message)
    {
        // No transport-level retry — application retry is the user's concern via IPipelineBehavior.
        // Commit the offset (Kafka offsets are sequential — not committing blocks all subsequent messages)
        // and publish to the dead-letter topic with error metadata.
        var messageType = this.GetMessageType(message) ?? "unknown";
        LogDeadLettering(this.Logger, messageType, null);
        await this.PublishToDeadLetterAsync(message);
    }

    // -----------------------------------------------------------------------
    // Outbound primitives
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override bool IsStarted =>
        this.producer is not null && this.sharedTopicName is not null;

    /// <inheritdoc />
    protected override async Task<BinaryData> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");

        LogSendingRequest(this.Logger, envelope.Type, null);

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingReplies[correlationId] = tcs;

        try
        {
            var headers = BuildOutboundHeaders();
            headers.Add("conduit-type", Encoding.UTF8.GetBytes(envelope.Type));
            headers.Add("conduit-reply-topic", Encoding.UTF8.GetBytes(this.replyTopicName!));
            headers.Add("conduit-correlation-id", Encoding.UTF8.GetBytes(correlationId));

            var message = new Message<string, byte[]>
            {
                Key = envelope.Type,
                Value = body.ToArray(),
                Headers = headers,
            };

            await this.producer!.ProduceAsync(this.sharedTopicName, message, cancellationToken);

            // Wait for reply with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(this.options.ResponseTimeout);

            using var registration = timeoutCts.Token.Register(() =>
                tcs.TrySetCanceled(timeoutCts.Token));

            var replyBytes = await tcs.Task;
            return new BinaryData(replyBytes);
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

        var headers = BuildOutboundHeaders();
        headers.Add("conduit-type", Encoding.UTF8.GetBytes(envelope.Type));

        var message = new Message<string, byte[]>
        {
            Key = envelope.Type,
            Value = body.ToArray(),
            Headers = headers,
        };

        await this.producer!.ProduceAsync(this.sharedTopicName, message, cancellationToken);
    }

    // -----------------------------------------------------------------------
    // Private helpers — Consumer loops
    // -----------------------------------------------------------------------

    private IConsumer<string, byte[]> CreateConsumer(string groupId)
    {
        var config = this.options.ConsumerConfig is not null
            ? new ConsumerConfig(this.options.ConsumerConfig)
            : new ConsumerConfig();

        config.BootstrapServers = this.options.BootstrapServers;
        config.GroupId = groupId;
        config.EnableAutoCommit = false;
        config.AutoOffsetReset = AutoOffsetReset.Earliest;

        return new ConsumerBuilder<string, byte[]>(config).Build();
    }

    private async Task ConsumeLoopAsync(
        IConsumer<string, byte[]> consumer,
        bool isCommandQueryConsumer,
        CancellationToken cancellationToken)
    {
        var relevantTypes = isCommandQueryConsumer
            ? this.commandQueryTypeNames
            : this.eventTypeNames;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromMilliseconds(100));
                }
                catch (ConsumeException)
                {
                    continue;
                }

                if (result is null)
                {
                    continue;
                }

                // Filter: only process message types relevant to this consumer
                var messageType = GetHeaderString(result.Message.Headers, "conduit-type");
                if (messageType is null || !relevantTypes.Contains(messageType))
                {
                    // Commit offset for irrelevant messages to avoid blocking
                    consumer.StoreOffset(result);
                    consumer.Commit(result);
                    continue;
                }

                var inboundMessage = new KafkaInboundMessage
                {
                    ConsumeResult = result,
                };
                await this.ProcessInboundMessageAsync(inboundMessage);

                // Commit offset after processing
                consumer.StoreOffset(result);
                consumer.Commit(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private Task ReplyConsumeLoopAsync(
        IConsumer<string, byte[]> consumer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromMilliseconds(100));
                }
                catch (ConsumeException)
                {
                    continue;
                }

                if (result is null)
                {
                    continue;
                }

                var correlationId = GetHeaderString(result.Message.Headers, "conduit-correlation-id");
                if (!string.IsNullOrEmpty(correlationId) &&
                    this.pendingReplies.TryRemove(correlationId, out var tcs))
                {
                    tcs.TrySetResult(result.Message.Value);
                }

                consumer.StoreOffset(result);
                consumer.Commit(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }

        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Private helpers — Cleanup and dead-letter
    // -----------------------------------------------------------------------

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        // Kafka has no native auto-delete topics. This background task periodically
        // checks and deletes stale topics for Developer/Test modes.
        var cleanupInterval = this.ConduitOptions.TopologyMode switch
        {
            TopologyMode.Developer => TimeSpan.FromHours(1),
            TopologyMode.Test => TimeSpan.FromMinutes(1),
            TopologyMode.Production or _ => TimeSpan.FromHours(1),
        };

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(cleanupInterval, cancellationToken);

                // Cleanup is handled by Kafka's retention.ms setting on the topics
                // (configured in KafkaTopologyManager). The background task here is
                // available for deleting the topics themselves if needed, but typically
                // retention handles message cleanup while topics persist.
                // For Test mode, we could delete topics after the TTL, but the retention
                // policy already ensures messages expire.
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async Task PublishToDeadLetterAsync(KafkaInboundMessage message)
    {
        if (this.producer is null || this.deadLetterTopicName is null)
        {
            return;
        }

        try
        {
            var consumeResult = message.ConsumeResult;

            var headers = new Headers();
            var originalType = this.GetMessageType(message);
            if (originalType is not null)
            {
                headers.Add("conduit-original-type", Encoding.UTF8.GetBytes(originalType));
            }

            var deadLetterMessage = new Message<string, byte[]>
            {
                Key = consumeResult.Message.Key,
                Value = consumeResult.Message.Value,
                Headers = headers,
            };

            await this.producer.ProduceAsync(this.deadLetterTopicName, deadLetterMessage);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Log but don't throw — the message is already lost from the main topic
            LogDeadLetterError(this.Logger, ex);
        }
    }

    private static Headers BuildOutboundHeaders()
    {
        var headers = new Headers();
        var context = MessageContext.Current;
        if (context is not null)
        {
            headers.Add("conduit-message-id", Encoding.UTF8.GetBytes(context.MessageId));

            if (context.CausationId is not null)
            {
                headers.Add("conduit-causation-id", Encoding.UTF8.GetBytes(context.CausationId));
            }

            headers.Add("conduit-conversation-id", Encoding.UTF8.GetBytes(context.ConversationId));
        }

        return headers;
    }

    private static string? GetHeaderString(Headers? headers, string key)
    {
        if (headers is null)
        {
            return null;
        }

        try
        {
            if (headers.TryGetLastBytes(key, out var bytes))
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // Header not found or invalid
        }

        return null;
    }
}
