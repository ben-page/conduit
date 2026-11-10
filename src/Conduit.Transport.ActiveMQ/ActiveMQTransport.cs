using System.Text;
using Amqp;
using Amqp.Framing;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.ActiveMQ;

/// <summary>
/// ActiveMQ Artemis transport extending <see cref="BrokerTransportBase{TMessage}"/>.
/// Inbound dispatch, lifecycle, and outbound serialization are handled by the base class.
/// This class implements the Artemis-specific primitives: AMQP 1.0 connection/session
/// management, two-address publishing (anycast for commands/queries, multicast for events),
/// exclusive reply queue correlation, and receiver link management.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1062:Validate arguments of public methods",
    Justification = "Protected override methods are called by the base class which validates parameters.")]
public sealed class ActiveMQTransport : BrokerTransportBase<ActiveMQInboundMessage>
{
    private readonly ActiveMQOptions options;

    private Connection? connection;
    private Session? session;
    private SenderLink? commandSender;
    private SenderLink? eventSender;
    private ReceiverLink? replyReceiver;
    private string? replyQueueAddress;
    private TopologyResult? topologyResult;

    private readonly List<ReceiverLink> consumerLinks = [];

    // Transport-specific logging delegates
    private static readonly Action<ILogger, string, Exception?> LogPublishingEvent =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(100, "PublishingEvent"),
            "Publishing event '{Type}' to Artemis multicast address");

    private static readonly Action<ILogger, string, Exception?> LogSendingRequest =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(101, "SendingRequest"),
            "Sending request '{Type}' to Artemis anycast address");

    private static readonly Action<ILogger, int, Exception?> LogStartingConsumers =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(105, "StartingConsumers"),
            "Starting Artemis consumers for {Count} subscribed type(s)");

    private static readonly Action<ILogger, Exception?> LogTransportStopping =
        LoggerMessage.Define(LogLevel.Information, new EventId(106, "TransportStopping"),
            "Stopping ActiveMQ Artemis transport");

    /// <summary>
    /// Creates a new ActiveMQ Artemis transport instance.
    /// </summary>
    public ActiveMQTransport(
        ActiveMQOptions options,
        ConduitOptions conduitOptions,
        IServiceProvider serviceProvider,
        ILogger<ActiveMQTransport> logger)
        : base(options, conduitOptions, serviceProvider, logger)
    {
        this.options = options;
    }

    /// <inheritdoc/>
    protected override string TransportName => "activemq";

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var address = new Address(this.options.ConnectionString);

        if (this.options.Username is not null)
        {
            address = new Address(
                address.Host,
                address.Port,
                this.options.Username,
                this.options.Password,
                address.Path,
                address.Scheme);
        }

        var factory = new ConnectionFactory();
        this.connection = await factory.CreateAsync(address);
        this.session = new Session(this.connection);
    }

    /// <inheritdoc />
    protected override async Task EnsureTopologyAsync(
        IReadOnlyList<string> eventTypeNames,
        IReadOnlyList<string> commandQueryTypeNames,
        CancellationToken cancellationToken)
    {
        var topologyManager = new ActiveMQTopologyManager(this.options, this.ConduitOptions, this.Logger);
        this.topologyResult = await topologyManager.EnsureTopologyAsync(
            this.session!, eventTypeNames, commandQueryTypeNames, cancellationToken);

        await this.EnsureSendersAndReplyQueueAsync(
            this.topologyResult.AnycastAddress, this.topologyResult.MulticastAddress);
    }

    /// <inheritdoc />
    protected override async Task EnsureOutboundTopologyAsync(CancellationToken cancellationToken)
    {
        var topologyManager = new ActiveMQTopologyManager(this.options, this.ConduitOptions, this.Logger);
        var (anycastAddress, multicastAddress) = await topologyManager.EnsureAddressesAsync(
            this.session!, cancellationToken);

        await this.EnsureSendersAndReplyQueueAsync(anycastAddress, multicastAddress);
    }

    private async Task EnsureSendersAndReplyQueueAsync(string anycastAddress, string multicastAddress)
    {
        // Create sender links for the two addresses
        this.commandSender = new SenderLink(
            this.session,
            "command-sender",
            anycastAddress);

        this.eventSender = new SenderLink(
            this.session,
            "event-sender",
            multicastAddress);

        // Create exclusive temporary reply queue using AMQP dynamic node.
        // The broker assigns the actual address — we capture it from the attach performative.
        var replySource = new Source
        {
            Dynamic = true,
        };

        var attachedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.replyReceiver = new ReceiverLink(
            this.session,
            "reply-receiver",
            replySource,
            (link, attach) =>
            {
                if (attach.Source is Source source && source.Address is not null)
                {
                    attachedTcs.TrySetResult(source.Address);
                }
                else
                {
                    attachedTcs.TrySetException(
                        new InvalidOperationException("Broker did not assign a dynamic reply queue address."));
                }
            });

        this.replyQueueAddress = await attachedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Set up reply message handler
        this.replyReceiver.Start(10, this.OnReplyReceived);
    }

    /// <inheritdoc />
    protected override Task StartConsumersAsync(CancellationToken cancellationToken)
    {
        var registry = this.ServiceProvider.GetRequiredService<ConduitTypeRegistry>();
        LogStartingConsumers(this.Logger, registry.ActiveEventTypeNames.Count + registry.CommandTypeNames.Count + registry.QueryTypeNames.Count, null);

        // Start default app event queue consumer (events — multicast)
        if (this.topologyResult!.HasAppEventQueue)
        {
            this.StartConsumer(
                this.topologyResult.AppEventQueueName,
                this.topologyResult.MulticastAddress,
                this.options.DefaultConcurrency);
        }

        // Start shared command/query queue consumers (unicast — competing consumers, default prefetch)
        foreach (var sharedQueueName in this.topologyResult.SharedCommandQueryQueues)
        {
            this.StartConsumer(
                sharedQueueName,
                this.topologyResult.AnycastAddress,
                this.options.DefaultConcurrency);
        }

        // Start pool event queue consumers (multicast, pool-specific prefetch)
        foreach (var (queueName, prefetchCount) in this.topologyResult.PoolEventQueues)
        {
            this.StartConsumer(
                queueName,
                this.topologyResult.MulticastAddress,
                prefetchCount);
        }

        // Start pool command/query queue consumers (unicast — competing consumers, pool-specific prefetch)
        foreach (var (queueName, prefetchCount) in this.topologyResult.PoolCommandQueryQueues)
        {
            this.StartConsumer(
                queueName,
                this.topologyResult.AnycastAddress,
                prefetchCount);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task StopReceivingAsync(CancellationToken cancellationToken)
    {
        LogTransportStopping(this.Logger, null);

        // Withdraw the consumer links' credit so the broker sends no more, but keep the links open: a
        // handler still running accepts its message on the link it arrived on. A message already in
        // flight under the old credit still arrives, and the base class hands it back.
        foreach (var receiverLink in this.consumerLinks)
        {
            try
            {
                if (!receiverLink.IsClosed)
                {
                    receiverLink.SetCredit(0, autoRestore: true);
                }
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best effort: a link that failed is closed below anyway
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task StopConsumersAsync(CancellationToken cancellationToken)
    {
        // The drain is over: closing a consumer link releases whatever is still unsettled on it (a
        // handler the deadline cut short), so the broker redelivers it
        foreach (var receiverLink in this.consumerLinks)
        {
            try
            {
                if (!receiverLink.IsClosed)
                {
                    await receiverLink.CloseAsync();
                }
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best-effort cleanup during shutdown
            }
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        // Close reply receiver
        if (this.replyReceiver is not null && !this.replyReceiver.IsClosed)
        {
            try
            {
                await this.replyReceiver.CloseAsync();
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best-effort cleanup
            }
        }

        // Close sender links
        if (this.commandSender is not null && !this.commandSender.IsClosed)
        {
            try
            {
                await this.commandSender.CloseAsync();
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best-effort cleanup
            }
        }

        if (this.eventSender is not null && !this.eventSender.IsClosed)
        {
            try
            {
                await this.eventSender.CloseAsync();
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best-effort cleanup
            }
        }

        // Close session and connection
        if (this.session is not null && !this.session.IsClosed)
        {
            try
            {
                await this.session.CloseAsync();
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best-effort cleanup
            }
        }

        if (this.connection is not null && !this.connection.IsClosed)
        {
            try
            {
                await this.connection.CloseAsync();
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
    protected override string? GetMessageType(ActiveMQInboundMessage message) =>
        message.Message.Properties?.Subject;

    /// <inheritdoc />
    protected override BinaryData GetMessageBody(ActiveMQInboundMessage message)
    {
        var body = message.Message.Body;
        if (body is byte[] bytes)
        {
            return new BinaryData(bytes);
        }

        if (body is string text)
        {
            return new BinaryData(Encoding.UTF8.GetBytes(text));
        }

        // Handle Amqp.Data sections
        if (message.Message.BodySection is Data dataSection)
        {
            return new BinaryData(dataSection.Binary);
        }

        return new BinaryData(Encoding.UTF8.GetBytes(body?.ToString() ?? "{}"));
    }

    /// <inheritdoc />
    protected override bool IsRequest(ActiveMQInboundMessage message) =>
        message.Message.ApplicationProperties?["ReplyTo"] is not null;

    /// <inheritdoc />
    protected override (string MessageId, string? CausationId, string ConversationId) ExtractContext(
        ActiveMQInboundMessage message)
    {
        var messageId = message.Message.Properties?.MessageId?.ToString()
            ?? Guid.NewGuid().ToString("N");
        var causationId = message.Message.ApplicationProperties?["CausationId"]?.ToString();
        var conversationId = message.Message.Properties?.CorrelationId?.ToString() ?? messageId;

        return (messageId, causationId, conversationId);
    }

    /// <inheritdoc />
    protected override async Task SendReplyAsync(ActiveMQInboundMessage message, BinaryData replyBody)
    {
        var replyAddress = message.Message.ApplicationProperties?["ReplyTo"]?.ToString();
        var correlationId = message.Message.ApplicationProperties?["ReplyCorrelationId"]?.ToString();

        if (string.IsNullOrEmpty(replyAddress) || string.IsNullOrEmpty(correlationId))
        {
            return;
        }

        var replySender = new SenderLink(this.session, $"reply-{Guid.NewGuid():N}", replyAddress);
        try
        {
            using var replyMessage = new Message(replyBody.ToArray())
            {
                Properties = new Properties
                {
                    CorrelationId = correlationId,
                },
            };

            await replySender.SendAsync(replyMessage);
        }
        finally
        {
            if (!replySender.IsClosed)
            {
                await replySender.CloseAsync();
            }
        }
    }

    /// <inheritdoc />
    protected override Task AcknowledgeAsync(ActiveMQInboundMessage message)
    {
        message.ReceiverLink.Accept(message.Message);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task AbandonAsync(ActiveMQInboundMessage message)
    {
        message.ReceiverLink.Release(message.Message);
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Outbound primitives
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override bool IsStarted =>
        this.commandSender is not null && this.eventSender is not null && this.replyQueueAddress is not null;

    /// <inheritdoc />
    protected override async Task<ReadOnlyMemory<byte>> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        IReadOnlyDictionary<string, string> headers,
        string correlationId,
        CancellationToken cancellationToken)
    {
        LogSendingRequest(this.Logger, envelope.Type, null);

        using var message = new Message(body.ToArray())
        {
            Properties = new Properties
            {
                Subject = envelope.Type,
                MessageId = headers.GetValueOrDefault(ConduitHeaders.MessageId)
                    ?? Guid.NewGuid().ToString("N"),
                CorrelationId = headers.GetValueOrDefault(ConduitHeaders.ConversationId),
            },
            ApplicationProperties = BuildOutboundApplicationProperties(headers),
        };

        message.ApplicationProperties["ReplyTo"] = this.replyQueueAddress;
        message.ApplicationProperties["ReplyCorrelationId"] = correlationId;

        await this.commandSender!.SendAsync(message);

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

        using var message = new Message(body.ToArray())
        {
            Properties = new Properties
            {
                Subject = envelope.Type,
                MessageId = headers.GetValueOrDefault(ConduitHeaders.MessageId)
                    ?? Guid.NewGuid().ToString("N"),
                CorrelationId = headers.GetValueOrDefault(ConduitHeaders.ConversationId),
            },
            ApplicationProperties = BuildOutboundApplicationProperties(headers),
        };

        await this.eventSender!.SendAsync(message);
    }

    /// <inheritdoc />
    public override async Task RedeliverAsync(
        DispatchEnvelope envelope,
        RedeliveryIds? ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.session is null)
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

        // Send directly to the app event queue using Artemis FQQN (address::queue).
        // This targets the specific queue rather than broadcasting via the multicast address.
        var multicastAddress = TopologyNaming.CreateSharedResourceName(this.ConduitOptions, "events", 256);
        var appEventQueueName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, 256);
        var fullyQualifiedQueueName = $"{multicastAddress}::{appEventQueueName}";
        var headers = BuildRedeliveryHeaders(ids);

        using var message = new Message(body.ToArray())
        {
            Properties = new Properties
            {
                Subject = envelope.Type,
                MessageId = headers[ConduitHeaders.MessageId],
                CorrelationId = headers.GetValueOrDefault(ConduitHeaders.ConversationId),
            },
            ApplicationProperties = BuildOutboundApplicationProperties(headers),
        };

        var redeliverSender = new SenderLink(this.session, $"dlq-redeliver-{Guid.NewGuid():N}", fullyQualifiedQueueName);
        try
        {
            await redeliverSender.SendAsync(message);
        }
        finally
        {
            await redeliverSender.CloseAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Trace context
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override string? ExtractMessageHeader(ActiveMQInboundMessage message, string key)
    {
        if (message.Message.ApplicationProperties?.Map is null)
        {
            return null;
        }

        return message.Message.ApplicationProperties.Map.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private void StartConsumer(string queueName, string addressName, int prefetchCount)
    {
        // Create receiver link targeting the specific queue on the address
        // Using FQQN (Fully Qualified Queue Name) format: address::queue
        var source = new Source
        {
            Address = $"{addressName}::{queueName}",
        };

        var receiverLink = new ReceiverLink(
            this.session,
            $"consumer-{queueName}-{Guid.NewGuid():N}"[..50],
            source,
            null);

        receiverLink.Start(prefetchCount, this.OnMessageReceived);
        this.consumerLinks.Add(receiverLink);
    }

    private void OnMessageReceived(IReceiverLink receiverLink, Message message)
    {
        var inboundMessage = new ActiveMQInboundMessage
        {
            ReceiverLink = (ReceiverLink)receiverLink,
            Message = message,
        };

        // Not awaited (the link's callback is synchronous): ProcessInboundMessageAsync handles
        // ack/abandon, and the base class tracks the call so stop can drain it
        _ = this.ProcessInboundMessageAsync(inboundMessage);
    }

    private void OnReplyReceived(IReceiverLink receiverLink, Message message)
    {
        var correlationId = message.Properties?.CorrelationId?.ToString();
        if (!string.IsNullOrEmpty(correlationId))
        {
            byte[] replyBytes;
            if (message.Body is byte[] bytes)
            {
                replyBytes = bytes;
            }
            else if (message.BodySection is Data dataSection)
            {
                replyBytes = dataSection.Binary;
            }
            else if (message.Body is string text)
            {
                replyBytes = Encoding.UTF8.GetBytes(text);
            }
            else
            {
                replyBytes = Encoding.UTF8.GetBytes(message.Body?.ToString() ?? "{}");
            }

            this.CompleteReply(correlationId, replyBytes);
        }

        ((ReceiverLink)receiverLink).Accept(message);
    }

    private static ApplicationProperties BuildOutboundApplicationProperties(
        IReadOnlyDictionary<string, string> headers)
    {
        var properties = new ApplicationProperties();

        foreach (var (key, value) in headers)
        {
            properties[key] = value;
        }

        // Historical on-wire name, preserved so ExtractContext keeps working unchanged.
        if (headers.TryGetValue(ConduitHeaders.CausationId, out var causationId))
        {
            properties["CausationId"] = causationId;
        }

        return properties;
    }
}
