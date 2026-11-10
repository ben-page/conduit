using System.Collections.Concurrent;
using System.Text;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

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

    // Names this instance's reply queue, which no other instance shares; replaced by a fresh id when another
    // connection holds the name (EnsureReplyQueueAsync)
    private string replyInstanceId;

    // Every reply queue name this transport declared: the client's automatic recovery leaves them alone
    private readonly ConcurrentDictionary<string, byte> replyQueueNames = new(StringComparer.Ordinal);

    // Outbound requests, events and redeliveries, and the topology declarations (publisher confirms on)
    private IChannel? publishChannel;

    // Replies to the requests this instance handles (publisher confirms on); none on a producer-only host
    private IChannel? replyPublishChannel;

    // Consumes this instance's reply queue
    private IChannel? replyConsumerChannel;

    // The reply address, read by every request: replaced when the reply queue moves to a fresh name
    private volatile string? replyQueueName;
    private TopologyResult? topologyResult;

    // One channel per inbound consumer; each delivery is acknowledged on the channel it arrived on
    private readonly List<(IChannel Channel, string ConsumerTag)> inboundConsumers = [];

    // A channel that publishes with confirms: BasicPublishAsync returns only once the broker acknowledged
    // the message and throws when it refuses it, so a message the broker never took is never reported sent.
    // Immutable, with no confirm rate limiter, so both publishing channels share it.
    private static readonly CreateChannelOptions ConfirmingChannel =
        new(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);

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

    private static readonly Action<ILogger, string, string, Exception?> LogReplyQueueLocked =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(107, "ReplyQueueLocked"),
            "Reply queue '{LockedQueue}' is held by another connection; replies now go to '{ReplyQueue}', " +
            "and a request still awaiting a reply on the old queue times out");

    private static readonly Action<ILogger, string, Exception?> LogReplyQueueNotRecovered =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(108, "ReplyQueueNotRecovered"),
            "Could not re-establish reply queue '{ReplyQueue}' after the connection recovered; requests will time out");

    /// <summary>
    /// Creates a new RabbitMQ transport instance.
    /// </summary>
    public RabbitMQTransport(
        RabbitMQOptions options,
        ConduitOptions conduitOptions,
        IServiceProvider serviceProvider,
        ILogger<RabbitMQTransport> logger)
        : base(options, conduitOptions, serviceProvider, logger)
    {
        this.options = options;
        this.replyInstanceId = options.InitialReplyQueueInstanceId ?? Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    protected override string TransportName => "rabbitmq";

    /// <summary>The broker connection once started (a test observes its automatic recovery).</summary>
    internal IConnection? Connection { get; private set; }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(this.options.ConnectionString),

            // The client's automatic recovery leaves the reply queue and its consumer alone: re-declaring the
            // exclusive queue fails RESOURCE_LOCKED while the broker still holds the old connection, and a
            // failure there abandons the whole recovery attempt. The transport re-establishes them itself once
            // the rest of the topology has recovered (OnConnectionRecoveredAsync).
            TopologyRecoveryFilter = new TopologyRecoveryFilter
            {
                QueueFilter = queue => !this.replyQueueNames.ContainsKey(queue.Name),
                ConsumerFilter = consumer => !this.replyQueueNames.ContainsKey(consumer.Queue),
            },
        };

        this.Connection = await factory.CreateConnectionAsync(cancellationToken);
        this.Connection.RecoverySucceededAsync += this.OnConnectionRecoveredAsync;
        this.publishChannel = await this.Connection.CreateChannelAsync(ConfirmingChannel, cancellationToken);

        // Replies go out on a channel of their own: a channel error closes the channel it happens on (an
        // outbound publish to an exchange that no longer exists, say), and it must not take the replies
        // to the requests this instance is handling down with it.
        if (!this.ConduitOptions.ProducerOnly)
        {
            this.replyPublishChannel = await this.Connection.CreateChannelAsync(ConfirmingChannel, cancellationToken);
        }
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

    /// <summary>
    /// Declares this instance's reply queue and consumes it, at start and again after every connection
    /// recovery. The queue is named by the client (<c>{system}-{app}-replies-{instanceId}</c>): a
    /// server-named one came back from recovery under a new name while every request still carried the old
    /// one in <c>ReplyTo</c>. It is exclusive, so no other client on the vhost can consume this instance's
    /// replies. When another connection holds the name (<c>RESOURCE_LOCKED</c>: a half-open old connection
    /// of this instance the broker has not timed out yet, or anyone else), the instance takes a fresh id and
    /// a fresh name, which every request from then on carries; a request still awaiting a reply on the old
    /// name times out, as any lost reply does.
    /// </summary>
    private async Task EnsureReplyQueueAsync(CancellationToken cancellationToken)
    {
        var queueName = RabbitMQTopologyManager.ReplyQueueName(this.ConduitOptions, this.replyInstanceId);

        IChannel channel;
        try
        {
            channel = await this.DeclareReplyQueueAsync(queueName, cancellationToken);
        }
        catch (OperationInterruptedException exception)
            when (exception.ShutdownReason?.ReplyCode == Constants.ResourceLocked)
        {
            this.replyInstanceId = Guid.NewGuid().ToString("N");
            var freshName = RabbitMQTopologyManager.ReplyQueueName(this.ConduitOptions, this.replyInstanceId);
            LogReplyQueueLocked(this.Logger, queueName, freshName, null);

            queueName = freshName;
            channel = await this.DeclareReplyQueueAsync(queueName, cancellationToken);
        }

        try
        {
            var replyConsumer = new AsyncEventingBasicConsumer(channel);
            replyConsumer.ReceivedAsync += this.OnReplyReceivedAsync;
            await channel.BasicConsumeAsync(
                queue: queueName,
                autoAck: true,
                consumer: replyConsumer,
                cancellationToken: cancellationToken);
        }
        catch
        {
            await CloseAndDisposeChannelAsync(channel);
            throw;
        }

        // The reply address every request reads from here on
        var previousChannel = this.replyConsumerChannel;
        this.replyConsumerChannel = channel;
        this.replyQueueName = queueName;

        // After a recovery: the client re-opened the previous channel, without its consumer
        if (previousChannel is not null)
        {
            await CloseAndDisposeChannelAsync(previousChannel);
        }
    }

    /// <summary>
    /// Declares the exclusive reply queue <paramref name="queueName"/> on a new channel and returns the
    /// channel. A failed declaration closes its channel (a channel error), so the channel is disposed and the
    /// exception rethrown.
    /// </summary>
    private async Task<IChannel> DeclareReplyQueueAsync(string queueName, CancellationToken cancellationToken)
    {
        // Known to the recovery filter before the client records the queue, so it never recovers it
        this.replyQueueNames.TryAdd(queueName, 0);

        var channel = await this.Connection!.CreateChannelAsync(cancellationToken: cancellationToken);
        try
        {
            await channel.QueueDeclareAsync(
                queue: queueName,
                durable: false,
                exclusive: true,
                autoDelete: true,
                arguments: null,
                noWait: false,
                cancellationToken: cancellationToken);

            return channel;
        }
        catch
        {
            await CloseAndDisposeChannelAsync(channel);
            throw;
        }
    }

    /// <summary>
    /// Re-establishes the reply queue once the client has recovered the connection and the rest of the
    /// topology. The client awaits its recovery handlers in order, and this one is subscribed first, so the
    /// reply queue is back before any handler subscribed later hears of the recovery.
    /// </summary>
    private async Task OnConnectionRecoveredAsync(object sender, AsyncEventArgs args)
    {
        // Recovered before the transport ever declared a reply queue: start declares it
        if (this.replyQueueName is null)
        {
            return;
        }

        try
        {
            await this.EnsureReplyQueueAsync(args.CancellationToken);
        }
#pragma warning disable CA1031
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // The client swallows a recovery handler's exception: log it, or no reply would arrive unexplained
            LogReplyQueueNotRecovered(this.Logger, this.replyQueueName, exception);
        }
    }

    /// <inheritdoc />
    protected override async Task StartConsumersAsync(CancellationToken cancellationToken)
    {
        var registry = this.ServiceProvider.GetRequiredService<ConduitTypeRegistry>();
        LogStartingConsumers(this.Logger, registry.ActiveEventTypeNames.Count, null);

        // UseRabbitMQ refused a concurrency outside 1..65535, so these casts never wrap
        var defaultConcurrency = (ushort)this.options.DefaultConcurrency;

        // Start default app queue consumer (events — multicast)
        var appQueueName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, 255);
        await this.StartConsumerAsync(appQueueName, defaultConcurrency, cancellationToken);

        // Start shared command/query queue consumers (unicast — competing consumers, default concurrency)
        foreach (var sharedQueueName in this.topologyResult!.SharedCommandQueryQueues)
        {
            await this.StartConsumerAsync(sharedQueueName, defaultConcurrency, cancellationToken);
        }

        // Start pool event queue consumers (multicast, pool-specific concurrency)
        foreach (var (queueName, concurrency) in this.topologyResult.PoolEventQueues)
        {
            await this.StartConsumerAsync(queueName, concurrency, cancellationToken);
        }

        // Start pool command/query queue consumers (unicast — competing consumers, pool-specific concurrency)
        foreach (var (queueName, concurrency) in this.topologyResult.PoolCommandQueryQueues)
        {
            await this.StartConsumerAsync(queueName, concurrency, cancellationToken);
        }
    }

    /// <inheritdoc />
    protected override async Task StopReceivingAsync(CancellationToken cancellationToken)
    {
        LogTransportStopping(this.Logger, null);

        // Cancel the inbound consumers only. Their channels stay open, since a handler still running
        // acknowledges on its delivery's channel, and so does the reply consumer, since a handler may
        // be awaiting a reply.
        foreach (var (channel, consumerTag) in this.inboundConsumers)
        {
            try
            {
                await channel.BasicCancelAsync(consumerTag, noWait: false, cancellationToken: cancellationToken);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best-effort cancellation during shutdown
            }
        }
    }

    /// <inheritdoc />
    protected override async Task StopConsumersAsync(CancellationToken cancellationToken)
    {
        // The drain is over: closing an inbound channel requeues whatever is still unacknowledged on
        // it (a handler the deadline cut short, a prefetched delivery never dispatched). The client's
        // close waits for a handler still running on the channel, so only the deadline bounds the wait.
        var closing = this.inboundConsumers.Select(consumer => CloseChannelAsync(consumer.Channel)).ToList();
        if (this.replyConsumerChannel is not null)
        {
            closing.Add(CloseChannelAsync(this.replyConsumerChannel));
        }

        await WaitUntilDeadlineAsync(closing, cancellationToken);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        foreach (var (channel, _) in this.inboundConsumers)
        {
            await CloseAndDisposeChannelAsync(channel);
        }

        if (this.replyConsumerChannel is not null)
        {
            await CloseAndDisposeChannelAsync(this.replyConsumerChannel);
        }

        if (this.replyPublishChannel is not null)
        {
            await CloseAndDisposeChannelAsync(this.replyPublishChannel);
        }

        if (this.publishChannel is not null)
        {
            await CloseAndDisposeChannelAsync(this.publishChannel);
        }

        if (this.Connection is not null)
        {
            try
            {
                if (this.Connection.IsOpen)
                {
                    await this.Connection.CloseAsync();
                }

                this.Connection.Dispose();
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
        new(message.Args.Body); // the transport's own copy (CopyDelivery), not the client's buffer

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
        if (this.replyPublishChannel is null || string.IsNullOrEmpty(message.Args.BasicProperties.ReplyTo))
        {
            return;
        }

        var replyProperties = new BasicProperties
        {
            ContentType = "application/json",
            CorrelationId = message.Args.BasicProperties.CorrelationId,
        };

        // Confirmed before the request is acknowledged: a reply the broker refused is logged by the base class
        await this.replyPublishChannel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: message.Args.BasicProperties.ReplyTo,
            mandatory: false,
            basicProperties: replyProperties,
            body: replyBody.ToMemory());
    }

    /// <inheritdoc />
    protected override Task AcknowledgeAsync(RabbitMQInboundMessage message) =>
        message.Channel.BasicAckAsync(message.Args.DeliveryTag, multiple: false).AsTask();

    /// <summary>
    /// Rejects the message without requeue, so its queue's dead-letter exchange routes it to the system's
    /// dead-letter queue (<c>{system}-dead-letter</c>) once, rather than handing it straight back to a
    /// consumer that fails it again, forever. Only what Conduit did not capture gets here: with a
    /// dead-letter store registered, the base class captures and acknowledges a failed message.
    /// </summary>
    protected override Task AbandonAsync(RabbitMQInboundMessage message) =>
        message.Channel.BasicNackAsync(message.Args.DeliveryTag, multiple: false, requeue: false).AsTask();

    /// <summary>
    /// Returns a message delivered after stop began draining to its queue (<c>BasicNack</c> with
    /// <c>requeue: true</c>), for another instance to take: it was never dispatched, so it belongs on the
    /// queue, not the dead-letter queue <see cref="AbandonAsync"/> would send it to.
    /// </summary>
    protected override Task RequeueAsync(RabbitMQInboundMessage message) =>
        message.Channel.BasicNackAsync(message.Args.DeliveryTag, multiple: false, requeue: true).AsTask();

    // -----------------------------------------------------------------------
    // Outbound primitives
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override bool IsStarted =>
        this.publishChannel is not null && this.replyQueueName is not null;

    /// <inheritdoc />
    protected override async Task<ReadOnlyMemory<byte>> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        IReadOnlyDictionary<string, string> headers,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var exchangeName = TopologyNaming.CreateSharedResourceName(this.ConduitOptions, 255);

        LogSendingRequest(this.Logger, envelope.Type, envelope.Type, null);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            Type = envelope.Type,
            CorrelationId = correlationId,
            ReplyTo = this.replyQueueName,
            MessageId = headers.GetValueOrDefault(ConduitHeaders.MessageId)
                ?? Guid.NewGuid().ToString("N"),
            Headers = ToRabbitHeaders(headers),
        };

        await this.publishChannel!.BasicPublishAsync(
            exchange: exchangeName,
            routingKey: envelope.Type,
            mandatory: false,
            basicProperties: properties,
            body: body.ToMemory(),
            cancellationToken: cancellationToken);

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

        var exchangeName = TopologyNaming.CreateSharedResourceName(this.ConduitOptions, 255);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            Type = envelope.Type,
            MessageId = headers.GetValueOrDefault(ConduitHeaders.MessageId)
                ?? Guid.NewGuid().ToString("N"),
            Headers = ToRabbitHeaders(headers),
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
    public override async Task RedeliverAsync(
        DispatchEnvelope envelope,
        RedeliveryIds? ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!this.IsStarted)
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

        var appQueueName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, 255);
        var headers = BuildRedeliveryHeaders(ids);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            Type = envelope.Type,
            MessageId = headers[ConduitHeaders.MessageId],
            Headers = ToRabbitHeaders(headers),
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
    protected override string? ExtractMessageHeader(RabbitMQInboundMessage message, string key) =>
        GetHeaderString(message.Args.BasicProperties.Headers, key);

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private async Task StartConsumerAsync(
        string queueName,
        ushort concurrency,
        CancellationToken cancellationToken)
    {
        // Up to `concurrency` handlers run at once: the broker keeps at most that many deliveries
        // unacknowledged on the channel (prefetch), and the client hands that many to the consumer at
        // once (dispatch concurrency). The prefetch alone only buffers: the client's default dispatch
        // concurrency of 1 ran the prefetched deliveries one after another.
        var channel = await this.Connection!.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: concurrency),
            cancellationToken);

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: concurrency,
            global: false,
            cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (sender, args) =>
            this.ProcessInboundMessageAsync(new RabbitMQInboundMessage(channel, CopyDelivery(args)));

        var tag = await channel.BasicConsumeAsync(
            queue: queueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken);

        this.inboundConsumers.Add((channel, tag));
    }

    /// <summary>
    /// The delivery with its own copy of the body. The client reuses a delivery's body buffer, and the
    /// message is read long after the first await (a dead-letter capture reads it once the handler has
    /// failed), so it is copied before anything awaits.
    /// </summary>
    private static BasicDeliverEventArgs CopyDelivery(BasicDeliverEventArgs args) =>
        new(
            args.ConsumerTag,
            args.DeliveryTag,
            args.Redelivered,
            args.Exchange,
            args.RoutingKey,
            args.BasicProperties,
            args.Body.ToArray(),
            args.CancellationToken);

    private static async Task CloseChannelAsync(IChannel channel)
    {
        try
        {
            if (channel.IsOpen)
            {
                await channel.CloseAsync();
            }
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // Best-effort cleanup
        }
    }

    private static async Task CloseAndDisposeChannelAsync(IChannel channel)
    {
        await CloseChannelAsync(channel);

        try
        {
            channel.Dispose();
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // Best-effort cleanup
        }
    }

    private Task OnReplyReceivedAsync(object sender, BasicDeliverEventArgs args)
    {
        // The client reuses the delivery's body buffer once this handler returns: copy it
        var correlationId = args.BasicProperties.CorrelationId;
        if (!string.IsNullOrEmpty(correlationId))
        {
            this.CompleteReply(correlationId, args.Body.ToArray());
        }

        return Task.CompletedTask;
    }

    private static Dictionary<string, object?> ToRabbitHeaders(
        IReadOnlyDictionary<string, string> headers)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in headers)
        {
            result[key] = Encoding.UTF8.GetBytes(value);
        }

        // Preserve the historical on-wire names alongside the canonical keys so existing
        // consumers and ExtractContext keep working unchanged.
        if (headers.TryGetValue(ConduitHeaders.CausationId, out var causationId))
        {
            result["CausationId"] = Encoding.UTF8.GetBytes(causationId);
        }

        if (headers.TryGetValue(ConduitHeaders.ConversationId, out var conversationId))
        {
            result["ConversationId"] = Encoding.UTF8.GetBytes(conversationId);
        }

        return result;
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
