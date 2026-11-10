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

    // One channel per inbound consumer; each delivery is acknowledged on the channel it arrived on
    private readonly List<(IChannel Channel, string ConsumerTag)> inboundConsumers = [];

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
        : base(options, conduitOptions, serviceProvider, logger)
    {
        this.options = options;
    }

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
        await this.replyChannel.BasicConsumeAsync(
            queue: this.replyQueueName,
            autoAck: true,
            consumer: replyConsumer,
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task StartConsumersAsync(CancellationToken cancellationToken)
    {
        var registry = this.ServiceProvider.GetRequiredService<ConduitTypeRegistry>();
        LogStartingConsumers(this.Logger, registry.ActiveEventTypeNames.Count, null);

        // Start default app queue consumer (events — multicast)
        var appQueueName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, 255);
        await this.StartConsumerAsync(appQueueName, (ushort)this.options.DefaultConcurrency, cancellationToken);

        // Start shared command/query queue consumers (unicast — competing consumers, default prefetch)
        foreach (var sharedQueueName in this.topologyResult!.SharedCommandQueryQueues)
        {
            await this.StartConsumerAsync(sharedQueueName, (ushort)this.options.DefaultConcurrency, cancellationToken);
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
        if (this.replyChannel is not null)
        {
            closing.Add(CloseChannelAsync(this.replyChannel));
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

        if (this.replyChannel is not null)
        {
            await CloseAndDisposeChannelAsync(this.replyChannel);
        }

        if (this.publishChannel is not null)
        {
            await CloseAndDisposeChannelAsync(this.publishChannel);
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
    public override async Task RedeliverAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!this.IsStarted)
        {
            throw new InvalidOperationException(
                "Transport has not been started. Ensure the host is running before redelivering messages.");
        }

        // Replay builds its properties by hand and deliberately bypasses the base class's
        // BuildOutboundHeaders — most importantly, it carries no conduit-source-app stamp. A
        // replayed event is redelivered on behalf of whoever originally published it; stamping
        // this host's ApplicationName would make the inbound self-published filter in
        // BrokerTransportBase.ProcessInboundMessageAsync drop the replayed copy the moment it
        // arrived, silently voiding the replay. Do not route replay through the base send path.
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
    protected override string? ExtractMessageHeader(RabbitMQInboundMessage message, string key) =>
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

        this.inboundConsumers.Add((channel, tag));
    }

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
