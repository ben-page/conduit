using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.AzureServiceBus;

/// <summary>
/// Azure Service Bus transport extending <see cref="BrokerTransportBase{TMessage}"/>.
/// Inbound dispatch, lifecycle, and outbound serialization are handled by the base class.
/// This class implements the ASB-specific primitives: session-based replies, topic publishing,
/// processor management, and topology creation.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1062:Validate arguments of public methods",
    Justification = "Protected override methods are called by the base class which validates parameters.")]
public sealed class AzureServiceBusTransport : BrokerTransportBase<ProcessMessageEventArgs>
{
    private readonly AzureServiceBusOptions options;
    private readonly List<ServiceBusProcessor> processors = [];

    // One sender per reply queue for the transport's lifetime, rather than a link attached and detached per reply
    private readonly ConcurrentDictionary<string, ServiceBusSender> replySenders = new(StringComparer.Ordinal);

    private ServiceBusClient? client;
    private ServiceBusSender? topicSender;

    // Transport-specific logging delegates
    private static readonly Action<ILogger, string, Exception?> LogSendingRequest =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(100, "SendingRequest"),
            "Sending request '{Type}' to topic");

    private static readonly Action<ILogger, string, Exception?> LogPublishingEvent =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(101, "PublishingEvent"),
            "Publishing event '{Type}' to topic");

    private static readonly Action<ILogger, int, Exception?> LogStartingProcessors =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(106, "StartingProcessors"),
            "Starting {Count} Azure Service Bus processor(s)");

    private static readonly Action<ILogger, int, Exception?> LogDiscoveredEventTypes =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(107, "DiscoveredEventTypes"),
            "Discovered {Count} subscribed event type(s) from type registry");

    private static readonly Action<ILogger, Exception?> LogStoppingProcessors =
        LoggerMessage.Define(LogLevel.Information, new EventId(108, "StoppingProcessors"),
            "Stopping Azure Service Bus processors");

    private static readonly Action<ILogger, string, string, Exception?> LogProcessingError =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(105, "ProcessingError"),
            "Error processing message on '{EntityPath}': {ErrorSource}");

    /// <summary>
    /// Creates a new Azure Service Bus transport.
    /// </summary>
    public AzureServiceBusTransport(
        AzureServiceBusOptions options,
        ConduitOptions conduitOptions,
        IServiceProvider serviceProvider,
        ILogger<AzureServiceBusTransport> logger)
        : base(options, conduitOptions, serviceProvider, logger)
    {
        this.options = options;
    }

    /// <inheritdoc/>
    protected override string TransportName => "asb";

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override Task ConnectAsync(CancellationToken cancellationToken)
    {
        var topicName = TopologyNaming.CreateSharedResourceName(this.ConduitOptions, 260);
        this.client = new ServiceBusClient(this.options.ConnectionString);
        this.topicSender = this.client.CreateSender(topicName);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task EnsureTopologyAsync(
        IReadOnlyList<string> eventTypeNames,
        IReadOnlyList<string> commandQueryTypeNames,
        CancellationToken cancellationToken)
    {
        var adminConnectionString = this.options.AdminConnectionString ?? this.options.ConnectionString;
        var adminClient = new ServiceBusAdministrationClient(adminConnectionString);
        var topologyManager = new AzureServiceBusTopologyManager(
            adminClient, this.options, this.ConduitOptions, this.Logger);

        LogDiscoveredEventTypes(this.Logger, eventTypeNames.Count, null);

        await topologyManager.EnsureTopologyAsync(
            eventTypeNames, commandQueryTypeNames, cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task EnsureOutboundTopologyAsync(CancellationToken cancellationToken)
    {
        var adminConnectionString = this.options.AdminConnectionString ?? this.options.ConnectionString;
        var adminClient = new ServiceBusAdministrationClient(adminConnectionString);
        var topologyManager = new AzureServiceBusTopologyManager(
            adminClient, this.options, this.ConduitOptions, this.Logger);

        await topologyManager.EnsureOutboundTopologyAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task StartConsumersAsync(CancellationToken cancellationToken)
    {
        var appQueueName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, 260);
        await this.StartProcessorAsync(appQueueName, this.options.DefaultConcurrency, cancellationToken);

        foreach (var pool in this.options.Pools)
        {
            var poolQueueName = TopologyNaming.CreateAppResourceName(
                this.ConduitOptions, pool.Name, 260);
            await this.StartProcessorAsync(poolQueueName, pool.Concurrency, cancellationToken);
        }

        LogStartingProcessors(this.Logger, this.processors.Count, null);
    }

    /// <inheritdoc />
    protected override async Task StopReceivingAsync(CancellationToken cancellationToken)
    {
        LogStoppingProcessors(this.Logger, null);

        // StopProcessingAsync stops receiving and leaves the receivers open for the in-flight handlers
        // to settle on, but it also awaits those handlers, past any token (a cancelled stop would
        // instead leave the processor receiving). So every processor is told to stop unconditionally,
        // and only this wait is bounded by the shutdown deadline.
        await WaitUntilDeadlineAsync(
            this.processors.Select(processor => processor.StopProcessingAsync(CancellationToken.None)),
            cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task StopConsumersAsync(CancellationToken cancellationToken)
    {
        // The drain is over: close the receivers. A message still unsettled is redelivered once its
        // lock expires. CloseAsync awaits a handler still running too, so it is bounded the same way.
        await WaitUntilDeadlineAsync(
            this.processors.Select(processor => processor.CloseAsync(CancellationToken.None)),
            cancellationToken);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        foreach (var processor in this.processors)
        {
            await processor.DisposeAsync();
        }

        this.processors.Clear();

        foreach (var replySender in this.replySenders.Values)
        {
            await replySender.DisposeAsync();
        }

        this.replySenders.Clear();

        if (this.topicSender is not null)
        {
            await this.topicSender.DisposeAsync();
        }

        if (this.client is not null)
        {
            await this.client.DisposeAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Inbound dispatch primitives
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override string? GetMessageType(ProcessMessageEventArgs message) =>
        message.Message.Subject;

    /// <inheritdoc />
    protected override BinaryData GetMessageBody(ProcessMessageEventArgs message) =>
        message.Message.Body;

    /// <inheritdoc />
    protected override bool IsRequest(ProcessMessageEventArgs message) =>
        !string.IsNullOrWhiteSpace(message.Message.ReplyToSessionId);

    /// <inheritdoc />
    protected override (string MessageId, string? CausationId, string ConversationId) ExtractContext(
        ProcessMessageEventArgs message)
    {
        var messageId = message.Message.MessageId ?? Guid.NewGuid().ToString("N");

        message.Message.ApplicationProperties.TryGetValue("CausationId", out var causationIdObj);
        var causationId = causationIdObj as string;

        var conversationId = message.Message.CorrelationId ?? messageId;

        return (messageId, causationId, conversationId);
    }

    // Replying and settling never pass ProcessMessageEventArgs.CancellationToken. StopProcessingAsync cancels
    // it as soon as stop begins, while the drain is still waiting for the handler to reply and settle: a
    // reply sent with it is lost, and a message whose settle fails stays locked until its lock expires, when
    // Service Bus redelivers it and the handler runs a second time.

    /// <inheritdoc />
    protected override async Task SendReplyAsync(ProcessMessageEventArgs message, BinaryData replyBody)
    {
        var replyMessage = this.CreateReplyMessage(message.Message, replyBody);
        var sender = await this.GetReplySenderAsync(message.Message.ReplyTo);
        await sender.SendMessageAsync(replyMessage, CancellationToken.None);
    }

    /// <inheritdoc />
    protected override Task AcknowledgeAsync(ProcessMessageEventArgs message) =>
        message.CompleteMessageAsync(message.Message, CancellationToken.None);

    /// <inheritdoc />
    protected override Task AbandonAsync(ProcessMessageEventArgs message) =>
        message.AbandonMessageAsync(message.Message, cancellationToken: CancellationToken.None);

    /// <summary>
    /// The reply to <paramref name="request"/>: in the request's own session, and discarded after
    /// <see cref="AzureServiceBusOptions.ReplyTimeToLive"/> (when there is one), by which time a request still
    /// waiting for it has given up, and nothing else reads its session.
    /// </summary>
    internal ServiceBusMessage CreateReplyMessage(ServiceBusReceivedMessage request, BinaryData replyBody)
    {
        var replyMessage = new ServiceBusMessage(replyBody)
        {
            SessionId = request.ReplyToSessionId,
            ContentType = "application/json",
        };

        if (this.options.ReplyTimeToLive is { } timeToLive)
        {
            replyMessage.TimeToLive = timeToLive;
        }

        return replyMessage;
    }

    // -----------------------------------------------------------------------
    // Outbound primitives
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override bool IsStarted => this.topicSender is not null;

    /// <inheritdoc />
    protected override async Task<ReadOnlyMemory<byte>> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        IReadOnlyDictionary<string, string> headers,
        string correlationId,
        CancellationToken cancellationToken)
    {
        LogSendingRequest(this.Logger, envelope.Type, null);

        var replyQueueName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, "replies", 260);

        // The correlation id is the reply's session: Service Bus correlates the reply itself
        var serviceBusMessage = new ServiceBusMessage(body)
        {
            Subject = envelope.Type,
            ContentType = "application/json",
            ReplyToSessionId = correlationId,
            ReplyTo = replyQueueName,
        };

        SeedOutboundMessageContext(serviceBusMessage, headers);

        await this.topicSender!.SendMessageAsync(serviceBusMessage, cancellationToken);

        return await this.AwaitSessionReplyAsync(envelope.Type, correlationId, cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task PublishEventAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        LogPublishingEvent(this.Logger, envelope.Type, null);

        var serviceBusMessage = new ServiceBusMessage(body)
        {
            Subject = envelope.Type,
            ContentType = "application/json",
        };

        SeedOutboundMessageContext(serviceBusMessage, headers);

        await this.topicSender!.SendMessageAsync(serviceBusMessage, cancellationToken);
    }

    /// <inheritdoc />
    public override async Task RedeliverAsync(
        DispatchEnvelope envelope,
        RedeliveryIds? ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.client is null)
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

        var appQueueName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, 260);

        var serviceBusMessage = new ServiceBusMessage(body)
        {
            Subject = envelope.Type,
            ContentType = "application/json",
        };

        SeedOutboundMessageContext(serviceBusMessage, BuildRedeliveryHeaders(ids));

        // Send directly to the app queue — bypasses topic routing
        await using var sender = this.client.CreateSender(appQueueName);
        await sender.SendMessageAsync(serviceBusMessage, cancellationToken);
    }

    // -----------------------------------------------------------------------
    // Trace context
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override string? ExtractMessageHeader(ProcessMessageEventArgs message, string key) =>
        message.Message.ApplicationProperties.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private async Task StartProcessorAsync(
        string queueName,
        int maxConcurrentCalls,
        CancellationToken cancellationToken)
    {
        var processorOptions = new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = maxConcurrentCalls,
            AutoCompleteMessages = false,
        };

        var processor = this.client!.CreateProcessor(queueName, processorOptions);
        processor.ProcessMessageAsync += this.ProcessInboundMessageAsync;
        processor.ProcessErrorAsync += this.ProcessErrorAsync;

        await processor.StartProcessingAsync(cancellationToken);
        this.processors.Add(processor);
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        LogProcessingError(this.Logger, args.EntityPath, args.ErrorSource.ToString(), args.Exception);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The sender for replies to <paramref name="replyTo"/>, created on the first reply to that queue and kept
    /// until the transport is disposed.
    /// </summary>
    private async ValueTask<ServiceBusSender> GetReplySenderAsync(string replyTo)
    {
        if (this.replySenders.TryGetValue(replyTo, out var sender))
        {
            return sender;
        }

        var created = this.client!.CreateSender(replyTo);
        if (this.replySenders.TryAdd(replyTo, created))
        {
            return created;
        }

        // A concurrent reply to the same queue added its sender first; this one never opened a link
        await created.DisposeAsync();
        return this.replySenders[replyTo];
    }

    /// <summary>
    /// Waits for a request's reply in the request's own session. Service Bus keeps the reply until the session
    /// is accepted, so no reply consumer feeds the base class's waiter; this races the session receive against
    /// that waiter only so that a stop, which fails every waiter, ends the request at once, as on every broker.
    /// </summary>
    private async Task<ReadOnlyMemory<byte>> AwaitSessionReplyAsync(
        string messageType,
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var received = this.ReceiveSessionReplyAsync(messageType, correlationId, waiting.Token);
        var stopped = this.AwaitReplyAsync(correlationId, waiting.Token);

        var first = await Task.WhenAny(received, stopped);

        // The other wait is cancelled and left to end on its own; a failure it still reports is observed
        await waiting.CancelAsync();
        ObserveFault(first == received ? stopped : received);

        return await first;
    }

    /// <summary>
    /// Receives a request's reply from its own session on the shared, session-enabled reply queue.
    /// </summary>
    private async Task<ReadOnlyMemory<byte>> ReceiveSessionReplyAsync(
        string messageType,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var replyQueueName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, "replies", 260);

        await using var sessionReceiver = await this.client!.AcceptSessionAsync(
            replyQueueName,
            sessionId,
            cancellationToken: cancellationToken);

        var replyMessage = await sessionReceiver.ReceiveMessageAsync(
            this.Options.ResponseTimeout, cancellationToken);

        if (replyMessage is null)
        {
            throw this.CreateReplyTimeoutException(messageType);
        }

        await sessionReceiver.CompleteMessageAsync(replyMessage, cancellationToken);

        return replyMessage.Body.ToMemory();
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static abandoned => _ = abandoned.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static void SeedOutboundMessageContext(
        ServiceBusMessage message,
        IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue(ConduitHeaders.MessageId, out var messageId))
        {
            message.MessageId = messageId;
        }

        if (headers.TryGetValue(ConduitHeaders.ConversationId, out var conversationId))
        {
            message.CorrelationId = conversationId;
        }

        // Historical on-wire name, preserved so ExtractContext keeps working unchanged.
        if (headers.TryGetValue(ConduitHeaders.CausationId, out var causationId))
        {
            message.ApplicationProperties["CausationId"] = causationId;
        }

        foreach (var (key, value) in headers)
        {
            message.ApplicationProperties[key] = value;
        }
    }
}
