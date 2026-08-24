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

    // All inbound consumer instances (default groups + pool groups), each running its own
    // sequential consume loop. Multiple instances share a group.id to scale partition
    // consumption — Kafka balances partitions across them. Held for graceful Close()/Dispose().
    private readonly List<IConsumer<string, byte[]>> messageConsumers = [];
    private IConsumer<string, byte[]>? replyConsumer;

    private CancellationTokenSource? consumerCts;
    private readonly List<Task> consumerTasks = [];
    private Task? replyConsumerTask;

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

    private static readonly Action<ILogger, int, int, Exception?> LogStartingConsumers =
        LoggerMessage.Define<int, int>(LogLevel.Information, new EventId(105, "StartingConsumers"),
            "Starting Kafka consumers: {ConsumerCount} instance(s) across {GroupCount} consumer group(s)");

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

    /// <inheritdoc/>
    protected override string EndpointName => this.options.EndpointName;

    /// <inheritdoc/>
    protected override string TransportName => "kafka";

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
        this.consumerCts = new CancellationTokenSource();
        var token = this.consumerCts.Token;

        var groupSpecs = this.BuildConsumerGroupSpecs();

        // Concurrency = N consumer instances per consumer group. Each instance runs the same
        // sequential consume loop; Kafka balances the shared topic's partitions across the
        // instances in a group and commits offsets per instance. This delivers real parallelism
        // (bounded by the topic's partition count) and preserves per-partition ordering — no
        // manual offset tracking. Pools are isolated because each is an independent consumer
        // group with its own instances; a slow pool handler cannot block another group.
        foreach (var spec in groupSpecs)
        {
            for (var instance = 0; instance < spec.InstanceCount; instance++)
            {
                var consumer = this.CreateConsumer(spec.GroupId);
                consumer.Subscribe(this.sharedTopicName);
                this.messageConsumers.Add(consumer);

                var ownedTypes = spec.OwnedTypes;
                this.consumerTasks.Add(Task.Run(
                    () => this.ConsumeLoopAsync(consumer, ownedTypes, token), token));
            }
        }

        var totalInstances = this.consumerTasks.Count;
        LogStartingConsumers(this.Logger, totalInstances, groupSpecs.Count, null);

        this.StartReplyConsumer();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the set of inbound consumer groups for this host. Pools become dedicated consumer
    /// groups that own only their assigned message types; the default groups own the complement
    /// (types not claimed by any pool). Command/query types use the shared (unicast) group id so
    /// exactly one instance across all hosts processes each message; event types use the per-app
    /// (multicast) group id so every app receives a copy. Each group runs its configured number of
    /// consumer instances (<see cref="BrokerTransportOptions.DefaultConcurrency"/> for the default
    /// groups, <see cref="ReceiverPoolOptions.Concurrency"/> for pool groups).
    /// </summary>
    private List<ConsumerGroupSpec> BuildConsumerGroupSpecs()
    {
        var specs = new List<ConsumerGroupSpec>();

        // Types claimed by any pool — removed from the default groups' owned sets.
        var pooledTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pool in this.options.Pools)
        {
            foreach (var messageType in pool.MessageTypes)
            {
                pooledTypes.Add(messageType);
            }
        }

        // Cap instance count at the shared topic's partition count: extra instances would sit
        // idle (Kafka assigns each partition to exactly one consumer in a group). This is the
        // inherent parallelism ceiling for a single group.
        var defaultConcurrency = ClampConcurrency(this.options.DefaultConcurrency);

        // Default command/query group — shared (unicast). Owns command/query types not pooled.
        // Only start if this host handles command/query types. A host with no command/query types
        // must not join the shared group — it would steal partitions and discard messages other
        // hosts need.
        var defaultCommandQueryTypes = this.commandQueryTypeNames
            .Where(typeName => !pooledTypes.Contains(typeName))
            .ToHashSet(StringComparer.Ordinal);
        if (defaultCommandQueryTypes.Count > 0)
        {
            specs.Add(new ConsumerGroupSpec(
                TopologyNaming.CreateSharedResourceName(this.ConduitOptions, 249),
                defaultCommandQueryTypes,
                defaultConcurrency));
        }

        // Default event group — per-app (multicast). Owns event types not pooled.
        var defaultEventTypes = this.eventTypeNames
            .Where(typeName => !pooledTypes.Contains(typeName))
            .ToHashSet(StringComparer.Ordinal);
        if (defaultEventTypes.Count > 0)
        {
            specs.Add(new ConsumerGroupSpec(
                TopologyNaming.CreateAppResourceName(this.ConduitOptions, 249),
                defaultEventTypes,
                defaultConcurrency));
        }

        // Pool groups — one dedicated consumer group per pool. Command/query types in a pool use
        // a shared (unicast) pool group; event types use a per-app (multicast) pool group. This
        // mirrors the default split while keeping the pool's processing isolated on its own group.
        foreach (var pool in this.options.Pools)
        {
            var poolConcurrency = ClampConcurrency(pool.Concurrency);

            var poolCommandQueryTypes = pool.MessageTypes
                .Where(this.commandQueryTypeNames.Contains)
                .ToHashSet(StringComparer.Ordinal);
            if (poolCommandQueryTypes.Count > 0)
            {
                specs.Add(new ConsumerGroupSpec(
                    TopologyNaming.CreateSharedResourceName(this.ConduitOptions, $"pool-{pool.Name}", 249),
                    poolCommandQueryTypes,
                    poolConcurrency));
            }

            var poolEventTypes = pool.MessageTypes
                .Where(this.eventTypeNames.Contains)
                .ToHashSet(StringComparer.Ordinal);
            if (poolEventTypes.Count > 0)
            {
                specs.Add(new ConsumerGroupSpec(
                    TopologyNaming.CreateAppResourceName(this.ConduitOptions, $"pool-{pool.Name}", 249),
                    poolEventTypes,
                    poolConcurrency));
            }
        }

        return specs;
    }

    /// <summary>
    /// Describes one inbound consumer group: its deterministic group id, the set of message types
    /// it owns (and exclusively processes — all other types are skipped and committed past), and
    /// the number of consumer instances to run in the group for concurrency.
    /// </summary>
    private sealed record ConsumerGroupSpec(
        string GroupId,
        HashSet<string> OwnedTypes,
        int InstanceCount);

    /// <summary>
    /// Clamps a requested concurrency to [1, shared-topic-partition-count]. Running more consumer
    /// instances than partitions yields no extra parallelism — the surplus stay idle.
    /// </summary>
    private static int ClampConcurrency(int requested) =>
        Math.Clamp(requested, 1, KafkaTopologyManager.SharedTopicPartitions);

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
        var tasks = new List<Task>(this.consumerTasks);

        if (this.replyConsumerTask is not null)
        {
            tasks.Add(this.replyConsumerTask);
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(timeout, cancellationToken));
        }

        // Close consumers gracefully to leave consumer groups (avoids rebalance timeout delay)
        foreach (var consumer in this.messageConsumers)
        {
            consumer.Close();
        }

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
        foreach (var consumer in this.messageConsumers)
        {
            consumer.Dispose();
        }

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
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");

        LogSendingRequest(this.Logger, envelope.Type, null);

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingReplies[correlationId] = tcs;

        try
        {
            var kafkaHeaders = BuildOutboundHeaders(headers);
            kafkaHeaders.Add("conduit-type", Encoding.UTF8.GetBytes(envelope.Type));
            kafkaHeaders.Add("conduit-reply-topic", Encoding.UTF8.GetBytes(this.replyTopicName!));
            kafkaHeaders.Add("conduit-correlation-id", Encoding.UTF8.GetBytes(correlationId));

            var message = new Message<string, byte[]>
            {
                Key = PartitionKey(headers),
                Value = body.ToArray(),
                Headers = kafkaHeaders,
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
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        LogPublishingEvent(this.Logger, envelope.Type, null);

        var kafkaHeaders = BuildOutboundHeaders(headers);
        kafkaHeaders.Add("conduit-type", Encoding.UTF8.GetBytes(envelope.Type));

        var message = new Message<string, byte[]>
        {
            Key = PartitionKey(headers),
            Value = body.ToArray(),
            Headers = kafkaHeaders,
        };

        await this.producer!.ProduceAsync(this.sharedTopicName, message, cancellationToken);
    }

    /// <inheritdoc />
    public override async Task RedeliverAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.producer is null || this.sharedTopicName is null)
        {
            throw new InvalidOperationException(
                "Transport has not been started. Ensure the host is running before redelivering messages.");
        }

        // Kafka has no direct-to-consumer-group mechanism — produce to the main topic.
        // All consumer groups see the message; inbox prevents duplicate processing by
        // non-target services (recommended for Kafka DLQ replay).
        var body = new BinaryData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload, ConduitRuntime.JsonOptions));

        var headers = new Headers
        {
            { "conduit-type", Encoding.UTF8.GetBytes(envelope.Type) },
            { "conduit-message-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")) },
        };

        var message = new Message<string, byte[]>
        {
            Key = Guid.NewGuid().ToString("N"),
            Value = body.ToArray(),
            Headers = headers,
        };

        await this.producer.ProduceAsync(this.sharedTopicName, message, cancellationToken);
    }

    /// <summary>
    /// Builds the Kafka partition key for an outbound message. Uses the dispatch's message id (a
    /// fresh GUID per dispatch) so messages — including those of the same type — distribute across
    /// the shared topic's partitions. This lets a consumer group with concurrency &gt; 1 process
    /// them in parallel. The type discriminator travels in the <c>conduit-type</c> header, not the
    /// key, so partition routing is independent of message type. There is no cross-message ordering
    /// contract between distinct dispatches, so distributing the key is safe.
    /// </summary>
    private static string PartitionKey(IReadOnlyDictionary<string, string> headers) =>
        headers.GetValueOrDefault(ConduitHeaders.MessageId) ?? Guid.NewGuid().ToString("N");

    // -----------------------------------------------------------------------
    // Trace context
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override string? ExtractMessageHeader(KafkaInboundMessage message, string key) =>
        GetHeaderString(message.ConsumeResult.Message.Headers, key);

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
        config.SessionTimeoutMs ??= 10_000;

        return new ConsumerBuilder<string, byte[]>(config).Build();
    }

    private async Task ConsumeLoopAsync(
        IConsumer<string, byte[]> consumer,
        HashSet<string> ownedTypes,
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

                // Filter: this group exclusively owns a set of message types. Skip (but commit
                // past) everything else so the owning group — and only that group — processes it.
                var messageType = GetHeaderString(result.Message.Headers, "conduit-type");
                if (messageType is null || !ownedTypes.Contains(messageType))
                {
                    try
                    {
                        consumer.StoreOffset(result);
                        consumer.Commit(result);
                    }
                    catch (KafkaException)
                    {
                        // Partition revoked during rebalance
                    }

                    continue;
                }

                var inboundMessage = new KafkaInboundMessage
                {
                    ConsumeResult = result,
                };

                await this.ProcessInboundMessageAsync(inboundMessage);

                // Commit offset after processing. StoreOffset/Commit can throw
                // if a rebalance revoked this partition during processing.
                // The message will be redelivered to the new partition owner.
                try
                {
                    consumer.StoreOffset(result);
                    consumer.Commit(result);
                }
                catch (KafkaException)
                {
                    // Partition was revoked during processing — safe to continue.
                    // The message will be redelivered to the new partition owner.
                }
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

                try
                {
                    consumer.StoreOffset(result);
                    consumer.Commit(result);
                }
                catch (KafkaException)
                {
                    // Group membership can change even for manually-assigned consumers.
                    // The reply was already delivered — offset commit is best-effort.
                }
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

    private static Headers BuildOutboundHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var result = new Headers();

        foreach (var (key, value) in headers)
        {
            result.Add(key, Encoding.UTF8.GetBytes(value));
        }

        return result;
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
