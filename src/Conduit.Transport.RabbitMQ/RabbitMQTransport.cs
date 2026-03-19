using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Conduit.Transport.RabbitMQ;

/// <summary>
/// RabbitMQ transport implementing <see cref="IConduitTransport"/> for outbound messaging
/// and <see cref="IHostedService"/> for inbound message consumption.
/// </summary>
/// <remarks>
/// Outbound: publishes commands/queries to topic exchange with routing key = FullTypeName
/// (with reply-to for request/response) and events to topic exchange with routing key = FullTypeName.
/// Inbound: consumes from per-app queue, creates DI scope per message, dispatches via IWireMediator.
/// </remarks>
public sealed class RabbitMQTransport : IConduitTransport, IHostedService, IAsyncDisposable
{
    private readonly RabbitMQOptions options;
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<RabbitMQTransport> logger;

    private IConnection? connection;
    private IChannel? publishChannel;
    private IChannel? replyChannel;
    private string? replyQueueName;
    private ConduitOptions? conduitOptions;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> pendingReplies = new();
    private readonly List<string> consumerTags = [];
    private readonly List<IChannel> consumerChannels = [];

    // High-performance logging delegates
    private static readonly Action<ILogger, string, Exception?> LogPublishingEvent =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(100, "PublishingEvent"),
            "Publishing event '{Type}' to RabbitMQ exchange");

    private static readonly Action<ILogger, string, string, Exception?> LogSendingRequest =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(101, "SendingRequest"),
            "Sending request '{Type}' to exchange with routing key '{RoutingKey}'");

    private static readonly Action<ILogger, string, Exception?> LogReceivedMessage =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(102, "ReceivedMessage"),
            "Received message '{Type}' from RabbitMQ");

    private static readonly Action<ILogger, string, Exception?> LogEventNacked =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(103, "EventNacked"),
            "Event '{Type}' nacked due to listener failure — broker will redeliver");

    private static readonly Action<ILogger, string, Exception?> LogMessageProcessingError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(104, "MessageProcessingError"),
            "Error processing message '{Type}'");

    private static readonly Action<ILogger, int, Exception?> LogStartingConsumers =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(105, "StartingConsumers"),
            "Starting RabbitMQ consumers for {Count} subscribed event type(s)");

    private static readonly Action<ILogger, Exception?> LogTransportStopping =
        LoggerMessage.Define(LogLevel.Information, new EventId(106, "TransportStopping"),
            "Stopping RabbitMQ transport");

    private static readonly Action<ILogger, string, Exception?> LogAutoCreateTopologyOverride =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(107, "AutoCreateTopologyOverride"),
            "AutoCreateTopology was false but TopologyMode is '{TopologyMode}' — overriding to true");

    /// <summary>
    /// Creates a new RabbitMQ transport instance.
    /// </summary>
    /// <param name="options">RabbitMQ connection and topology options.</param>
    /// <param name="serviceProvider">The application service provider for resolving IWireMediator per scope.</param>
    /// <param name="logger">Logger instance.</param>
    public RabbitMQTransport(
        RabbitMQOptions options,
        IServiceProvider serviceProvider,
        ILogger<RabbitMQTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options;
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.publishChannel is null || this.replyQueueName is null || this.conduitOptions is null)
        {
            throw new InvalidOperationException("RabbitMQ transport has not been started.");
        }

        var exchangeName = TopologyNaming.CreateSharedResourceName(this.conduitOptions, 255);
        var correlationId = Guid.NewGuid().ToString("N");

        LogSendingRequest(this.logger, envelope.Type, envelope.Type, null);

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingReplies[correlationId] = tcs;

        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(
                envelope.Payload, ConduitRuntime.JsonOptions);

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                Type = envelope.Type,
                CorrelationId = correlationId,
                ReplyTo = this.replyQueueName,
                MessageId = MessageContext.Current?.MessageId ?? Guid.NewGuid().ToString("N"),
                Headers = BuildOutboundHeaders(),
            };

            await this.publishChannel.BasicPublishAsync(
                exchange: exchangeName,
                routingKey: envelope.Type,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken);

            // Wait for reply with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            using var registration = timeoutCts.Token.Register(() =>
                tcs.TrySetCanceled(timeoutCts.Token));

            var replyBody = await tcs.Task;

            var result = JsonSerializer.Deserialize<DispatchResult<JsonElement?>>(
                replyBody, ConduitRuntime.JsonOptions);

            if (result?.Error is not null)
            {
                throw new InvalidOperationException(
                    $"Remote dispatch failed: [{result.Error.Code}] {result.Error.Message}");
            }

            return result?.Data;
        }
        finally
        {
            this.pendingReplies.TryRemove(correlationId, out _);
        }
    }

    /// <inheritdoc />
    public async Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.publishChannel is null || this.conduitOptions is null)
        {
            throw new InvalidOperationException("RabbitMQ transport has not been started.");
        }

        LogPublishingEvent(this.logger, envelope.Type, null);

        var exchangeName = TopologyNaming.CreateSharedResourceName(this.conduitOptions, 255);

        var body = JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload, ConduitRuntime.JsonOptions);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            Type = envelope.Type,
            MessageId = MessageContext.Current?.MessageId ?? Guid.NewGuid().ToString("N"),
            Headers = BuildOutboundHeaders(),
        };

        await this.publishChannel.BasicPublishAsync(
            exchange: exchangeName,
            routingKey: envelope.Type,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        this.conduitOptions = this.ResolveConduitOptions();

        var factory = new ConnectionFactory
        {
            Uri = new Uri(this.options.ConnectionString),
        };

        this.connection = await factory.CreateConnectionAsync(cancellationToken);

        // Channel for publishing
        this.publishChannel = await this.connection.CreateChannelAsync(
            cancellationToken: cancellationToken);

        // Get registered types from compile-time registry
        var registry = this.serviceProvider.GetRequiredService<ConduitTypeRegistry>();
        var subscribedEventTypes = registry.GetEventListenerNames();
        var handledCommandQueryTypes = registry.GetCommandHandlerNames()
            .Concat(registry.GetQueryHandlerNames()).ToList();

        // Non-production modes force AutoCreateTopology on
        var autoCreate = this.options.AutoCreateTopology;
        if (!autoCreate && this.conduitOptions.TopologyMode != TopologyMode.Production)
        {
            autoCreate = true;
            LogAutoCreateTopologyOverride(
                this.logger, this.conduitOptions.TopologyMode.ToString(), null);
        }

        // Declare topology
        if (autoCreate)
        {
            var topologyManager = new RabbitMQTopologyManager(this.options, this.conduitOptions);
            await topologyManager.EnsureTopologyAsync(
                this.publishChannel, subscribedEventTypes, handledCommandQueryTypes, cancellationToken);
        }

        // Set up reply queue (exclusive auto-delete)
        this.replyChannel = await this.connection.CreateChannelAsync(
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

        LogStartingConsumers(this.logger, subscribedEventTypes.Count, null);

        // Start default app queue consumer
        var appQueueName = TopologyNaming.CreateAppResourceName(this.conduitOptions, 255);
        await this.StartConsumerAsync(appQueueName, this.options.DefaultPrefetchCount, cancellationToken);

        // Start pool consumers
        foreach (var pool in this.options.Pools)
        {
            var poolQueueName = TopologyNaming.CreateAppResourceName(
                this.conduitOptions, pool.Name, 255);
            await this.StartConsumerAsync(poolQueueName, pool.PrefetchCount, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        LogTransportStopping(this.logger, null);

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

        // Cancel pending replies
        foreach (var pending in this.pendingReplies.Values)
        {
            pending.TrySetCanceled(cancellationToken);
        }

        this.pendingReplies.Clear();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
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
        consumer.ReceivedAsync += (sender, args) => this.OnMessageReceivedAsync(channel, args);

        var tag = await channel.BasicConsumeAsync(
            queue: queueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken);

        this.consumerTags.Add(tag);
        this.consumerChannels.Add(channel);
    }

    private async Task OnMessageReceivedAsync(IChannel channel, BasicDeliverEventArgs args)
    {
        var typeName = args.BasicProperties.Type;
        if (string.IsNullOrEmpty(typeName))
        {
            // Missing type header — ack and discard
            await channel.BasicAckAsync(args.DeliveryTag, multiple: false);
            return;
        }

        LogReceivedMessage(this.logger, typeName, null);

        var isReplyExpected = !string.IsNullOrEmpty(args.BasicProperties.ReplyTo);

        try
        {
            await using var scope = this.serviceProvider.CreateAsyncScope();
            var wireMediator = scope.ServiceProvider.GetRequiredService<IWireMediator>();

            // Seed MessageContext from inbound message properties
            var messageId = args.BasicProperties.MessageId ?? Guid.NewGuid().ToString("N");
            var causationId = GetHeaderString(args.BasicProperties.Headers, "CausationId");
            var conversationId = args.BasicProperties.CorrelationId ?? messageId;

            MessageContext.StartDispatchFromTransport(messageId, causationId, conversationId);

            var payload = JsonSerializer.Deserialize<JsonElement>(args.Body.Span, ConduitRuntime.JsonOptions);
            var envelope = new DispatchEnvelope(typeName, payload);

            if (isReplyExpected)
            {
                // Command/query — dispatch and send reply
                var result = await wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);
                await this.SendReplyAsync(args.BasicProperties, result);
                await channel.BasicAckAsync(args.DeliveryTag, multiple: false);
            }
            else
            {
                // Event — dispatch to local listeners
                try
                {
                    await wireMediator.HandleWireEventAsync(envelope, CancellationToken.None);
                    await channel.BasicAckAsync(args.DeliveryTag, multiple: false);
                }
                catch (EventListenerException)
                {
                    // Listener failure — nack for broker redelivery
                    LogEventNacked(this.logger, typeName, null);
                    await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true);
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogMessageProcessingError(this.logger, typeName, ex);
            // Always ack to prevent poison message loops for unexpected errors
            await channel.BasicAckAsync(args.DeliveryTag, multiple: false);
        }
    }

    private async Task SendReplyAsync(IReadOnlyBasicProperties requestProperties, DispatchResult<object> result)
    {
        if (this.publishChannel is null || string.IsNullOrEmpty(requestProperties.ReplyTo))
        {
            return;
        }

        var replyBody = JsonSerializer.SerializeToUtf8Bytes(result, ConduitRuntime.JsonOptions);

        var replyProperties = new BasicProperties
        {
            ContentType = "application/json",
            CorrelationId = requestProperties.CorrelationId,
        };

        await this.publishChannel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: requestProperties.ReplyTo,
            mandatory: false,
            basicProperties: replyProperties,
            body: replyBody);
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

    /// <summary>
    /// Resolves ConduitOptions from DI, ensuring ApplicationName is set.
    /// Falls back to IHostEnvironment.ApplicationName if not set explicitly.
    /// </summary>
    private ConduitOptions ResolveConduitOptions()
    {
        var resolvedOptions = this.serviceProvider.GetRequiredService<ConduitOptions>();

        if (string.IsNullOrWhiteSpace(resolvedOptions.ApplicationName))
        {
            var hostEnvironment = this.serviceProvider.GetService<IHostEnvironment>();
            if (!string.IsNullOrWhiteSpace(hostEnvironment?.ApplicationName))
            {
                resolvedOptions.ApplicationName = hostEnvironment.ApplicationName;
            }
            else
            {
                throw new InvalidOperationException(
                    "ApplicationName must be set on ConduitBuilder or IHostEnvironment must be available. " +
                    "Set conduit.ApplicationName in your AddConduit() configuration.");
            }
        }

        return resolvedOptions;
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
