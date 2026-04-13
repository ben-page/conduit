using System.Collections.Concurrent;
using System.Text;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Conduit.Transport.RabbitMQ;

/// <summary>
/// RabbitMQ transport extending <see cref="BrokerTransportBase{TMessage}"/>.
/// Inbound dispatch, lifecycle, and outbound serialization are handled by the base class.
/// This class implements the RabbitMQ-specific primitives: AMQP channels, topic exchange
/// publishing, reply queue correlation, and consumer management.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1062:Validate arguments of public methods",
    Justification = "Protected override methods are called by the base class which validates parameters.")]
public sealed class RabbitMQTransport : BrokerTransportBase<RabbitMQInboundMessage>
{
    private readonly RabbitMQOptions options;

    private IConnection? connection;
    private IChannel? publishChannel;
    private IChannel? replyChannel;
    private string? replyQueueName;
    private TopologyResult? topologyResult;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> pendingReplies = new();
    private readonly List<string> consumerTags = [];
    private readonly List<IChannel> consumerChannels = [];

    [ThreadStatic]
    private static Dictionary<string, string>? pendingTraceHeaders;

    // Transport-specific logging delegates
    private static readonly Action<ILogger, string, Exception?> LogPublishingEvent =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(100, "PublishingEvent"),
            "Publishing event '{Type}' to RabbitMQ exchange");

    private static readonly Action<ILogger, string, string, Exception?> LogSendingRequest =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(101, "SendingRequest"),
            "Sending request '{Type}' to exchange with routing key '{RoutingKey}'");

    private static readonly Action<ILogger, int, Exception?> LogStartingConsumers =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(105, "StartingConsumers"),
            "Starting RabbitMQ consumers for {Count} subscribed event type(s)");

    private static readonly Action<ILogger, Exception?> LogTransportStopping =
        LoggerMessage.Define(LogLevel.Information, new EventId(106, "TransportStopping"),
            "Stopping RabbitMQ transport");

    /// <summary>
    /// Creates a new RabbitMQ transport instance.
    /// </summary>
    public RabbitMQTransport(
        RabbitMQOptions options,
        ConduitOptions conduitOptions,
        IServiceProvider serviceProvider,
        ILogger<RabbitMQTransport> logger)
        : base(conduitOptions, serviceProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <inheritdoc/>
    protected override string EndpointName => this.options.EndpointName;

    /// <inheritdoc/>
    protected override string TransportName => "rabbitmq";

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(this.options.ConnectionString),
        };

        this.connection = await factory.CreateConnectionAsync(cancellationToken);
        this.publishChannel = await this.connection.CreateChannelAsync(
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task EnsureTopologyAsync(
        IReadOnlyList<string> eventTypeNames,
        IReadOnlyList<string> commandQueryTypeNames,
        CancellationToken cancellationToken)
    {
        var topologyManager = new RabbitMQTopologyManager(this.options, this.ConduitOptions);
        this.topologyResult = await topologyManager.EnsureTopologyAsync(
            this.publishChannel!, eventTypeNames, commandQueryTypeNames, cancellationToken);

        await this.EnsureReplyQueueAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task EnsureOutboundTopologyAsync(CancellationToken cancellationToken)
    {
        var topologyManager = new RabbitMQTopologyManager(this.options, this.ConduitOptions);
        await topologyManager.EnsureExchangeAsync(this.publishChannel!, cancellationToken);

        await this.EnsureReplyQueueAsync(cancellationToken);
    }

    private async Task EnsureReplyQueueAsync(CancellationToken cancellationToken)
    {
        // Set up reply queue (exclusive auto-delete)
        this.replyChannel = await this.connection!.CreateChannelAsync(
            cancellationToken: cancellationToken);
        var replyDeclare = await this.replyChannel.QueueDeclareAsync(
            queue: string.Empty,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);
        this.replyQueueName = replyDeclare.QueueName;

        // Consume replies
        var replyConsumer = new AsyncEventingBasicConsumer(this.replyChannel);
        replyConsumer.ReceivedAsync += this.OnReplyReceivedAsync;
        var replyTag = await this.replyChannel.BasicConsumeAsync(
            queue: this.replyQueueName,
            autoAck: true,
            consumer: replyConsumer,
            cancellationToken: cancellationToken);
        this.consumerTags.Add(replyTag);
        this.consumerChannels.Add(this.replyChannel);
    }

    /// <inheritdoc />
    protected override async Task StartConsumersAsync(CancellationToken cancellationToken)
    {
        var registry = this.ServiceProvider.GetRequiredService<ConduitTypeRegistry>();
        LogStartingConsumers(this.Logger, registry.EventTypeNames.Count, null);

        // Start default app queue consumer (events — multicast)
        var appQueueName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, 255);
        await this.StartConsumerAsync(appQueueName, this.options.DefaultPrefetchCount, cancellationToken);

        // Start shared command/query queue consumers (unicast — competing consumers, default prefetch)
        foreach (var sharedQueueName in this.topologyResult!.SharedCommandQueryQueues)
        {
            await this.StartConsumerAsync(sharedQueueName, this.options.DefaultPrefetchCount, cancellationToken);
        }

        // Start pool event queue consumers (multicast, pool-specific prefetch)
        foreach (var (queueName, prefetchCount) in this.topologyResult.PoolEventQueues)
        {
            await this.StartConsumerAsync(queueName, prefetchCount, cancellationToken);
        }

        // Start pool command/query queue consumers (unicast — competing consumers, pool-specific prefetch)
        foreach (var (queueName, prefetchCount) in this.topologyResult.PoolCommandQueryQueues)
        {
            await this.StartConsumerAsync(queueName, prefetchCount, cancellationToken);
        }
    }

    /// <inheritdoc />
    protected override async Task StopConsumersAsync(CancellationToken cancellationToken)
    {
        LogTransportStopping(this.Logger, null);

        // Cancel all consumers
        for (var i = 0; i < this.consumerTags.Count; i++)
        {
            try
            {
                await this.consumerChannels[i].BasicCancelAsync(
                    this.consumerTags[i], noWait: false, cancellationToken: cancellationToken);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best-effort cancellation during shutdown
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
    public override async ValueTask DisposeAsync()
    {
        foreach (var channel in this.consumerChannels)
        {
            try
            {
                if (channel.IsOpen)
                {
                    await channel.CloseAsync();
                }

                channel.Dispose();
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best-effort cleanup
            }
        }

        if (this.publishChannel is not null)
        {
            try
            {
                if (this.publishChannel.IsOpen)
                {
                    await this.publishChannel.CloseAsync();
                }

                this.publishChannel.Dispose();
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best-effort cleanup
            }
        }

        if (this.connection is not null)
        {
            try
            {
                if (this.connection.IsOpen)
                {
                    await this.connection.CloseAsync();
                }

                this.connection.Dispose();
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best-effort cleanup
            }
        }
    }

    // -----------------------------------------------------------------------
    // Inbound dispatch primitives
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override string? GetMessageType(RabbitMQInboundMessage message) =>
        message.Args.BasicProperties.Type;

    /// <inheritdoc />
    protected override BinaryData GetMessageBody(RabbitMQInboundMessage message) =>
        new(message.Args.Body.ToArray());

    /// <inheritdoc />
    protected override bool IsRequest(RabbitMQInboundMessage message) =>
        !string.IsNullOrEmpty(message.Args.BasicProperties.ReplyTo);

    /// <inheritdoc />
    protected override (string MessageId, string? CausationId, string ConversationId) ExtractContext(
        RabbitMQInboundMessage message)
    {
        var messageId = message.Args.BasicProperties.MessageId ?? Guid.NewGuid().ToString("N");
        var causationId = GetHeaderString(message.Args.BasicProperties.Headers, "CausationId");
        var conversationId = GetHeaderString(message.Args.BasicProperties.Headers, "ConversationId") ?? messageId;

        return (messageId, causationId, conversationId);
    }

    /// <inheritdoc />
    protected override async Task SendReplyAsync(RabbitMQInboundMessage message, BinaryData replyBody)
    {
        if (this.publishChannel is null || string.IsNullOrEmpty(message.Args.BasicProperties.ReplyTo))
        {
            return;
        }

        var replyProperties = new BasicProperties
        {
            ContentType = "application/json",
            CorrelationId = message.Args.BasicProperties.CorrelationId,
        };

        await this.publishChannel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: message.Args.BasicProperties.ReplyTo,
            mandatory: false,
            basicProperties: replyProperties,
            body: replyBody.ToMemory());
    }

    /// <inheritdoc />
    protected override Task AcknowledgeAsync(RabbitMQInboundMessage message) =>
        message.Channel.BasicAckAsync(message.Args.DeliveryTag, multiple: false).AsTask();

    /// <inheritdoc />
    protected override Task AbandonAsync(RabbitMQInboundMessage message) =>
        message.Channel.BasicNackAsync(message.Args.DeliveryTag, multiple: false, requeue: true).AsTask();

    // -----------------------------------------------------------------------
    // Outbound primitives
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override bool IsStarted =>
        this.publishChannel is not null && this.replyQueueName is not null;

    /// <inheritdoc />
    protected override async Task<BinaryData> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        CancellationToken cancellationToken)
    {
        var exchangeName = TopologyNaming.CreateSharedResourceName(this.ConduitOptions, 255);
        var correlationId = Guid.NewGuid().ToString("N");

        LogSendingRequest(this.Logger, envelope.Type, envelope.Type, null);

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingReplies[correlationId] = tcs;

        try
        {
            var properties = new BasicProperties
            {
                ContentType = "application/json",
                Type = envelope.Type,
                CorrelationId = correlationId,
                ReplyTo = this.replyQueueName,
                MessageId = MessageContext.Current?.MessageId ?? Guid.NewGuid().ToString("N"),
                Headers = BuildOutboundHeaders(),
            };

            await this.publishChannel!.BasicPublishAsync(
                exchange: exchangeName,
                routingKey: envelope.Type,
                mandatory: false,
                basicProperties: properties,
                body: body.ToMemory(),
                cancellationToken: cancellationToken);

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

        var exchangeName = TopologyNaming.CreateSharedResourceName(this.ConduitOptions, 255);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            Type = envelope.Type,
            MessageId = MessageContext.Current?.MessageId ?? Guid.NewGuid().ToString("N"),
            Headers = BuildOutboundHeaders(),
        };

        await this.publishChannel!.BasicPublishAsync(
            exchange: exchangeName,
            routingKey: envelope.Type,
            mandatory: false,
            basicProperties: properties,
            body: body.ToMemory(),
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public override async Task RedeliverAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!this.IsStarted)
        {
            throw new InvalidOperationException(
                "Transport has not been started. Ensure the host is running before redelivering messages.");
        }

        var body = new BinaryData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload, ConduitRuntime.JsonOptions));

        var appQueueName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, 255);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            Type = envelope.Type,
            MessageId = Guid.NewGuid().ToString("N"),
        };

        // Publish directly to the app queue via the default exchange — bypasses topic routing
        await this.publishChannel!.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: appQueueName,
            mandatory: false,
            basicProperties: properties,
            body: body.ToMemory(),
            cancellationToken: cancellationToken);
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
    protected override string? ExtractTraceHeader(RabbitMQInboundMessage message, string key) =>
        GetHeaderString(message.Args.BasicProperties.Headers, key);

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private async Task StartConsumerAsync(
        string queueName,
        ushort prefetchCount,
        CancellationToken cancellationToken)
    {
        var channel = await this.connection!.CreateChannelAsync(
            cancellationToken: cancellationToken);

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: prefetchCount,
            global: false,
            cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (sender, args) =>
            this.ProcessInboundMessageAsync(new RabbitMQInboundMessage(channel, args));

        var tag = await channel.BasicConsumeAsync(
            queue: queueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken);

        this.consumerTags.Add(tag);
        this.consumerChannels.Add(channel);
    }

    private Task OnReplyReceivedAsync(object sender, BasicDeliverEventArgs args)
    {
        var correlationId = args.BasicProperties.CorrelationId;
        if (!string.IsNullOrEmpty(correlationId) &&
            this.pendingReplies.TryRemove(correlationId, out var tcs))
        {
            tcs.TrySetResult(args.Body.ToArray());
        }

        return Task.CompletedTask;
    }

    private static Dictionary<string, object?> BuildOutboundHeaders()
    {
        var headers = new Dictionary<string, object?>();
        var context = MessageContext.Current;
        if (context is not null)
        {
            if (context.CausationId is not null)
            {
                headers["CausationId"] = Encoding.UTF8.GetBytes(context.CausationId);
            }

            headers["ConversationId"] = Encoding.UTF8.GetBytes(context.ConversationId);
        }

        if (pendingTraceHeaders is not null)
        {
            foreach (var (key, value) in pendingTraceHeaders)
            {
                headers[key] = Encoding.UTF8.GetBytes(value);
            }

            pendingTraceHeaders = null;
        }

        return headers;
    }

    private static string? GetHeaderString(IDictionary<string, object?>? headers, string key)
    {
        if (headers is null)
        {
            return null;
        }

        if (!headers.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string s => s,
            null => null,
            _ => value.ToString(),
        };
    }
}
