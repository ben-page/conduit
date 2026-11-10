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
/// exclusive reply queue correlation, receiver link management, and reconnection: AMQPNetLite
/// does not reconnect, so when the connection is lost the transport builds a new one.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1062:Validate arguments of public methods",
    Justification = "Protected override methods are called by the base class which validates parameters.")]
public sealed class ActiveMQTransport : BrokerTransportBase<ActiveMQInboundMessage>
{
    private readonly ActiveMQOptions options;

    // Guards current, started, stopRequested and reconnecting: a lost connection, a reconnection
    // installing its replacement and stop each decide under it, so stop always wins over a reconnection
    private readonly Lock stateLock = new();
    private readonly CancellationTokenSource stopCancellation = new();
    private volatile ActiveMQConnection? current;
    private bool started;
    private bool stopRequested;
    private Task reconnecting = Task.CompletedTask;
    private TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    private IReadOnlyList<string> subscribedEventTypes = [];
    private IReadOnlyList<string> handledCommandQueryTypes = [];
    private TopologyResult? topologyResult;

    // Transport-specific logging delegates
    private static readonly Action<ILogger, string, Exception?> LogPublishingEvent =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(100, "PublishingEvent"),
            "Publishing event '{Type}' to Artemis multicast address");

    private static readonly Action<ILogger, string, Exception?> LogSendingRequest =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(101, "SendingRequest"),
            "Sending request '{Type}' to Artemis anycast address");

    private static readonly Action<ILogger, string, Exception?> LogSendingFireAndForgetCommand =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(102, "SendingFireAndForgetCommand"),
            "Sending fire-and-forget command '{Type}' to Artemis anycast address");

    private static readonly Action<ILogger, int, Exception?> LogStartingConsumers =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(105, "StartingConsumers"),
            "Starting Artemis consumers for {Count} subscribed type(s)");

    private static readonly Action<ILogger, Exception?> LogTransportStopping =
        LoggerMessage.Define(LogLevel.Information, new EventId(106, "TransportStopping"),
            "Stopping ActiveMQ Artemis transport");

    private static readonly Action<ILogger, string, string, Exception?> LogConnectionLost =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(110, "ConnectionLost"),
            "Lost the connection to Artemis for endpoint '{Endpoint}' ({Reason})");

    private static readonly Action<ILogger, string, int, Exception?> LogReconnected =
        LoggerMessage.Define<string, int>(LogLevel.Information, new EventId(111, "Reconnected"),
            "Reconnected to Artemis for endpoint '{Endpoint}' after {Attempts} attempt(s)");

    private static readonly Action<ILogger, int, string, TimeSpan, Exception?> LogReconnectFailed =
        LoggerMessage.Define<int, string, TimeSpan>(LogLevel.Warning, new EventId(112, "ReconnectFailed"),
            "Reconnection attempt {Attempt} to Artemis for endpoint '{Endpoint}' failed; retrying in {Delay}");

    private static readonly Action<ILogger, string, Exception?> LogInboundMessageFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(113, "InboundMessageFailed"),
            "Processing Artemis message '{Type}' failed outside its dispatch; a message left unsettled is delivered " +
            "again once its link closes");

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

    /// <summary>
    /// Completes when the transport next finishes reconnecting: a new connection with its topology,
    /// sender links, reply queue and consumers in place.
    /// </summary>
    internal Task NextReconnection => Volatile.Read(ref this.reconnected).Task;

    /// <summary>Whether the transport has a connection it can send on (<see cref="IsStarted"/>).</summary>
    internal bool IsConnected => this.IsStarted;

    private ConduitTypeRegistry TypeRegistry =>
        field ??= this.ServiceProvider.GetRequiredService<ConduitTypeRegistry>();

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = await ActiveMQConnection.OpenAsync(this.options, this.OnConnectionLost, cancellationToken);

        lock (this.stateLock)
        {
            this.current = connection;
        }
    }

    /// <inheritdoc />
    protected override async Task EnsureTopologyAsync(
        IReadOnlyList<string> eventTypeNames,
        IReadOnlyList<string> commandQueryTypeNames,
        CancellationToken cancellationToken)
    {
        // Kept for a reconnection, which rebuilds the same topology on its new connection
        this.subscribedEventTypes = eventTypeNames;
        this.handledCommandQueryTypes = commandQueryTypeNames;

        await this.EnsureInboundTopologyAsync(this.current!, cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task EnsureOutboundTopologyAsync(CancellationToken cancellationToken)
    {
        await this.EnsureProducerTopologyAsync(this.current!, cancellationToken);
        this.MarkStarted();
    }

    /// <inheritdoc />
    protected override Task StartConsumersAsync(CancellationToken cancellationToken)
    {
        var registry = this.TypeRegistry;
        LogStartingConsumers(this.Logger, registry.ActiveEventTypeNames.Count + registry.CommandTypeNames.Count + registry.QueryTypeNames.Count, null);

        this.StartConsumers(this.current!);
        this.MarkStarted();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task StopReceivingAsync(CancellationToken cancellationToken)
    {
        LogTransportStopping(this.Logger, null);

        // Stop wins: from here no lost connection starts a reconnection, and one in progress installs
        // nothing; cancelling also aborts whatever connection it is still building
        ActiveMQConnection? connection;
        lock (this.stateLock)
        {
            this.stopRequested = true;
            connection = this.current;
        }

        await this.stopCancellation.CancelAsync();

        // Keep the consumer links open: a handler still running accepts its message on the link it arrived on
        connection?.StopReplenishingConsumerCredit();
    }

    /// <inheritdoc />
    protected override async Task StopConsumersAsync(CancellationToken cancellationToken)
    {
        // The drain is over: closing a consumer link releases whatever is still unsettled on it (a
        // handler the deadline cut short), so the broker redelivers it. The links of a connection lost
        // meanwhile are closed already, and the broker released their deliveries when it went.
        if (this.current is { } connection)
        {
            await WaitUntilDeadlineAsync(connection.CloseConsumerLinks(), cancellationToken);
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 1)
        {
            return;
        }

        ActiveMQConnection? connection;
        Task reconnection;
        lock (this.stateLock)
        {
            this.stopRequested = true;
            connection = this.current;
            this.current = null;
            reconnection = this.reconnecting;
        }

        await this.stopCancellation.CancelAsync();

        // A reconnection never throws, and gives up within seconds once stop is cancelled (the longest
        // step it cannot cancel is a management send, bounded at 10 seconds); the bound is a backstop, so
        // nothing a broker does can hold the host's shutdown
        await reconnection.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (connection is not null)
        {
            await connection.DisposeAsync();
        }

        this.stopCancellation.Dispose();
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

        using var replyMessage = NewMessage(replyBody, new Properties { CorrelationId = correlationId });

        // Held for the send: the link is never evicted or closed under it
        var lease = this.CurrentConnection().ReplySenders.Acquire(replyAddress);
        try
        {
            // AMQPNetLite gives up on a send the broker has not settled within a minute
            await lease.Link.SendAsync(replyMessage);
        }
        catch (Exception)
        {
            // The next reply to that address attaches a fresh link; other replies on this one finish first
            lease.Discard();
            throw;
        }
        finally
        {
            lease.Release();
        }
    }

    /// <inheritdoc />
    protected override Task AcknowledgeAsync(ActiveMQInboundMessage message)
    {
        message.ReceiverLink.Accept(message.Message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Settles a failed delivery as <c>modified</c> with <c>delivery-failed</c>: Artemis counts it as a
    /// delivery attempt and redelivers it, until its address's <c>max-delivery-attempts</c> (10 by default)
    /// moves it to the dead-letter address (<c>DLQ</c> on a stock broker).
    /// </summary>
    protected override Task AbandonAsync(ActiveMQInboundMessage message)
    {
        message.ReceiverLink.Modify(message.Message, deliveryFailed: true, undeliverableHere: false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Hands back a message delivered after stop began draining as <c>released</c>: Artemis returns it to
    /// the queue for another consumer without counting a delivery attempt, since nothing about it failed.
    /// </summary>
    protected override Task RequeueAsync(ActiveMQInboundMessage message)
    {
        message.ReceiverLink.Release(message.Message);
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Outbound primitives
    // -----------------------------------------------------------------------

    /// <summary>
    /// Whether a connection is up with its sender links and reply queue in place: false from the moment
    /// the connection is lost until a reconnection replaces it.
    /// </summary>
    protected override bool IsStarted => this.current is { IsReady: true };

    /// <inheritdoc />
    protected override async Task<ReadOnlyMemory<byte>> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        IReadOnlyDictionary<string, string> headers,
        string correlationId,
        CancellationToken cancellationToken)
    {
        LogSendingRequest(this.Logger, envelope.Type, null);

        // One connection for the request and the reply queue it names
        var connection = this.CurrentConnection();

        using var message = NewMessage(
            body,
            new Properties
            {
                Subject = envelope.Type,
                MessageId = headers.GetValueOrDefault(ConduitHeaders.MessageId)
                    ?? Guid.NewGuid().ToString("N"),
                CorrelationId = headers.GetValueOrDefault(ConduitHeaders.ConversationId),
            },
            BuildOutboundApplicationProperties(headers));

        message.ApplicationProperties["ReplyTo"] = connection.ReplyAddress;
        message.ApplicationProperties["ReplyCorrelationId"] = correlationId;

        await connection.CommandSender!.SendAsync(message);

        return await this.AwaitReplyAsync(correlationId, cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task PublishEventAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        // A routed [FireAndForget] command comes this way too, sent without a reply: it goes to the
        // anycast address, where its type's queue takes it, as a request would. Every queue on the
        // multicast address filters to event types, so it would reach none there.
        var connection = this.CurrentConnection();
        var isEvent = this.TypeRegistry.EventTypeNames.Contains(envelope.Type);
        if (isEvent)
        {
            LogPublishingEvent(this.Logger, envelope.Type, null);
        }
        else
        {
            LogSendingFireAndForgetCommand(this.Logger, envelope.Type, null);
        }

        using var message = NewMessage(
            body,
            new Properties
            {
                Subject = envelope.Type,
                MessageId = headers.GetValueOrDefault(ConduitHeaders.MessageId)
                    ?? Guid.NewGuid().ToString("N"),
                CorrelationId = headers.GetValueOrDefault(ConduitHeaders.ConversationId),
            },
            BuildOutboundApplicationProperties(headers));

        await (isEvent ? connection.EventSender! : connection.CommandSender!).SendAsync(message);
    }

    /// <inheritdoc />
    public override async Task RedeliverAsync(
        DispatchEnvelope envelope,
        RedeliveryIds? ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.current is not { } connection)
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

        // Send directly to the queue this app takes the type from, by its fully qualified name
        // (address::queue), rather than broadcasting via the address. Every queue's filter admits only
        // its own types, and a message sent to a queue that leaves its type out is dropped.
        var registry = this.TypeRegistry;
        var isCommandOrQuery = registry.CommandTypeNames.Contains(envelope.Type, StringComparer.Ordinal)
            || registry.QueryTypeNames.Contains(envelope.Type, StringComparer.Ordinal);
        var fullyQualifiedQueueName = new ActiveMQTopologyManager(this.options, this.ConduitOptions, this.Logger)
            .RedeliveryQueue(envelope.Type, isCommandOrQuery);
        var headers = BuildRedeliveryHeaders(ids);

        using var message = NewMessage(
            body,
            new Properties
            {
                Subject = envelope.Type,
                MessageId = headers[ConduitHeaders.MessageId],
                CorrelationId = headers.GetValueOrDefault(ConduitHeaders.ConversationId),
            },
            BuildOutboundApplicationProperties(headers));

        var redeliverSender = new SenderLink(
            connection.Session, ActiveMQTopologyManager.LinkName("dlq-redeliver"), fullyQualifiedQueueName);
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
    // Connection, topology and reconnection
    // -----------------------------------------------------------------------

    private ActiveMQConnection CurrentConnection() =>
        this.current ?? throw new InvalidOperationException(
            $"The transport for endpoint '{this.EndpointName}' lost its connection to the broker and is reconnecting.");

    private async Task EnsureInboundTopologyAsync(ActiveMQConnection connection, CancellationToken cancellationToken)
    {
        var topologyManager = new ActiveMQTopologyManager(this.options, this.ConduitOptions, this.Logger);
        var topology = await topologyManager.EnsureTopologyAsync(
            connection.Session, this.subscribedEventTypes, this.handledCommandQueryTypes, cancellationToken);

        await connection.AttachSendersAsync(
            topology.AnycastAddress, topology.MulticastAddress, this.OnReplyReceived, cancellationToken);
        this.topologyResult = topology;
    }

    private async Task EnsureProducerTopologyAsync(ActiveMQConnection connection, CancellationToken cancellationToken)
    {
        var topologyManager = new ActiveMQTopologyManager(this.options, this.ConduitOptions, this.Logger);
        var (anycastAddress, multicastAddress) = await topologyManager.EnsureAddressesAsync(
            connection.Session, cancellationToken);

        await connection.AttachSendersAsync(anycastAddress, multicastAddress, this.OnReplyReceived, cancellationToken);
    }

    private void StartConsumers(ActiveMQConnection connection)
    {
        // Each consumer link targets its queue by fully qualified name (address::queue)
        var topology = this.topologyResult!;

        // Default app event queue consumer (events — multicast)
        if (topology.HasAppEventQueue)
        {
            connection.StartConsumer(
                $"{topology.MulticastAddress}::{topology.AppEventQueueName}",
                this.options.DefaultConcurrency,
                this.OnMessageReceived);
        }

        // Shared command/query queue consumers (unicast — competing consumers, default prefetch)
        foreach (var sharedQueueName in topology.SharedCommandQueryQueues)
        {
            connection.StartConsumer(
                $"{topology.AnycastAddress}::{sharedQueueName}", this.options.DefaultConcurrency, this.OnMessageReceived);
        }

        // Pool event queue consumers (multicast, pool-specific prefetch)
        foreach (var (queueName, prefetchCount) in topology.PoolEventQueues)
        {
            connection.StartConsumer($"{topology.MulticastAddress}::{queueName}", prefetchCount, this.OnMessageReceived);
        }

        // Pool command/query queue consumers (unicast — competing consumers, pool-specific prefetch)
        foreach (var (queueName, prefetchCount) in topology.PoolCommandQueryQueues)
        {
            connection.StartConsumer($"{topology.AnycastAddress}::{queueName}", prefetchCount, this.OnMessageReceived);
        }
    }

    /// <summary>
    /// Marks the start sequence finished: from here a lost connection is rebuilt. One lost while the
    /// host was still starting is rebuilt now.
    /// </summary>
    private void MarkStarted()
    {
        // Its close's callback may not have run yet, and once current moves on that callback reports nothing
        if (this.MarkStartedUnderLock() is { } lostBeforeStarted)
        {
            this.ReportConnectionLost(lostBeforeStarted);
        }
    }

    // Sets started, and starts rebuilding a connection lost before it was set; returns that connection
    private ActiveMQConnection? MarkStartedUnderLock()
    {
        lock (this.stateLock)
        {
            this.started = true;
            if (this.current is not { IsLost: true } lost)
            {
                return null;
            }

            if (!this.stopRequested)
            {
                this.BeginReconnecting(lost);
            }

            return lost;
        }
    }

    /// <summary>
    /// Called by <see cref="ActiveMQConnection"/>, on an AMQPNetLite thread, when the connection or one of
    /// its links closed without the transport closing it.
    /// </summary>
    private void OnConnectionLost(ActiveMQConnection connection)
    {
        bool isCurrent;
        lock (this.stateLock)
        {
            // Not current: a connection already replaced, one a reconnection is still building (whose
            // attempt then fails), or one MarkStarted already took over, which reported it
            isCurrent = ReferenceEquals(connection, this.current);
            if (isCurrent && this.started && !this.stopRequested)
            {
                this.BeginReconnecting(connection);
            }
        }

        if (isCurrent)
        {
            this.ReportConnectionLost(connection);
        }
    }

    /// <summary>
    /// Logs <c>ConnectionLost</c> and fails the requests waiting for a reply, once per lost connection,
    /// whichever of <see cref="OnConnectionLost"/> and <see cref="MarkStarted"/> sees the loss first.
    /// </summary>
    private void ReportConnectionLost(ActiveMQConnection connection)
    {
        if (!connection.TryClaimLossReport())
        {
            return;
        }

        var error = connection.LostError;
        var reason = error is null
            ? "closed by the broker"
            : string.IsNullOrEmpty(error.Description) ? error.Condition.ToString() : $"{error.Condition}: {error.Description}";
        LogConnectionLost(this.Logger, this.EndpointName, reason, null);

        // Its reply queue went with it, so no reply a request is waiting for can arrive any more
        this.FailPendingReplies(new InvalidOperationException(
            $"The connection to the broker for endpoint '{this.EndpointName}' was lost before the reply arrived."));
    }

    // Under stateLock: the transport is not started until the reconnection installs a new connection
    private void BeginReconnecting(ActiveMQConnection lost)
    {
        this.current = null;
        this.reconnecting = Task.Run(() => this.ReconnectAsync(lost));
    }

    /// <summary>
    /// Builds a new connection with its topology, sender links, reply queue and consumers until one is
    /// installed or stop begins, waiting 1, 2, 4, 8, 16, then 30 seconds before each attempt. Never throws.
    /// </summary>
    private async Task ReconnectAsync(ActiveMQConnection lost)
    {
        await lost.DisposeAsync();

        var stopToken = this.stopCancellation.Token;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Task.Delay(ReconnectDelay(attempt), stopToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

#pragma warning disable CA1031 // One failed attempt must not end the reconnection loop; an attempt stop overtook ends it quietly.
            try
            {
                if (await this.RebuildAsync(stopToken))
                {
                    LogReconnected(this.Logger, this.EndpointName, attempt, null);
                    Interlocked.Exchange(
                        ref this.reconnected,
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                }

                // Installed, or stop won
                return;
            }
            catch (Exception exception) when (!stopToken.IsCancellationRequested)
            {
                LogReconnectFailed(this.Logger, attempt, this.EndpointName, ReconnectDelay(attempt + 1), exception);
            }
            catch (Exception)
            {
                // Stop overtook the attempt
                return;
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// One reconnection attempt: opens a connection, rebuilds the topology, sender links and reply queue
    /// on it, and installs it with its consumers. True once installed; false when stop began first.
    /// </summary>
    /// <exception cref="Exception">The attempt failed; the connection it opened is closed.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The finally block disposes the connection on every path but the one that installs it; "
            + "the transport then owns it and disposes it when it is lost or the transport is disposed.")]
    private async Task<bool> RebuildAsync(CancellationToken stopToken)
    {
        ActiveMQConnection? connection = null;
        try
        {
            connection = await ActiveMQConnection.OpenAsync(this.options, this.OnConnectionLost, stopToken);

            // Stop aborts the connection, failing at once whatever is still waiting on it
            using (stopToken.Register(static state => ((ActiveMQConnection)state!).Abort(), connection))
            {
                if (this.ConduitOptions.ProducerOnly)
                {
                    await this.EnsureProducerTopologyAsync(connection, stopToken);
                }
                else
                {
                    await this.EnsureInboundTopologyAsync(connection, stopToken);
                }
            }

            if (!this.TryInstall(connection))
            {
                return false;
            }

            // The transport owns it now
            connection = null;
            return true;
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="connection"/> the transport's and starts its consumers, unless stop began
    /// (false) or the connection was lost while it was being built (throws, so the caller retries).
    /// </summary>
    private bool TryInstall(ActiveMQConnection connection)
    {
        lock (this.stateLock)
        {
            if (this.stopRequested)
            {
                return false;
            }

            if (connection.IsLost)
            {
                throw new InvalidOperationException("The connection was lost while the transport was rebuilding it.");
            }

            // Current before the consumers start, so a handler's reply finds the connection
            this.current = connection;
            try
            {
                if (!this.ConduitOptions.ProducerOnly)
                {
                    this.StartConsumers(connection);
                }
            }
            catch
            {
                this.current = null;
                throw;
            }

            return true;
        }
    }

    private static TimeSpan ReconnectDelay(int attempt) =>
        TimeSpan.FromSeconds(attempt >= 6 ? 30 : 1 << (attempt - 1));

    private void OnMessageReceived(IReceiverLink receiverLink, Message message)
    {
        var inboundMessage = new ActiveMQInboundMessage
        {
            ReceiverLink = (ReceiverLink)receiverLink,
            Message = message,
        };

        // Not awaited (the link's callback is synchronous): ProcessInboundMessageAsync handles
        // ack/abandon, and the base class tracks the call so stop can drain it
        _ = this.ProcessInboundAndObserveAsync(inboundMessage);
    }

    /// <summary>
    /// Runs the inbound dispatch and observes what escapes it (a settlement the link refused), which nothing else awaits:
    /// it is logged at Error (<c>InboundMessageFailed</c>, EventId 113), not left to the unobserved-task handler. The
    /// message is not settled again here: whatever settled it already stands, and one left unsettled is redelivered by
    /// Artemis once its link closes.
    /// </summary>
    private async Task ProcessInboundAndObserveAsync(ActiveMQInboundMessage inboundMessage)
    {
        try
        {
            await this.ProcessInboundMessageAsync(inboundMessage);
        }
#pragma warning disable CA1031 // Nothing else observes this task: what escapes the dispatch is logged, never settled twice.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogInboundMessageFailed(this.Logger, this.GetMessageType(inboundMessage) ?? "(none)", exception);
        }
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

    /// <summary>
    /// A message to send, durable: every message goes out through here, requests, events, replies and
    /// redeliveries alike, with an AMQP header whose <c>durable</c> flag is set (AMQP's default, with no
    /// header, is false). Artemis writes a durable message to its journal when a durable queue (Production)
    /// takes it, so it outlives a broker restart; a non-durable queue (Developer and Test modes, the reply
    /// queue) holds it in memory as it would any other.
    /// </summary>
    private static Message NewMessage(
        BinaryData body,
        Properties properties,
        ApplicationProperties? applicationProperties = null) =>
        new(body.ToArray())
        {
            Header = new Header { Durable = true },
            Properties = properties,
            ApplicationProperties = applicationProperties,
        };

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
