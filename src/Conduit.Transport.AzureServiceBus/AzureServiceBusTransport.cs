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

    private ServiceBusClient? client;
    private ServiceBusSender? topicSender;

    [ThreadStatic]
    private static Dictionary<string, string>? pendingTraceHeaders;

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

    private static readonly Action<ILogger, string, Exception?> LogReplyTimeout =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(109, "ReplyTimeout"),
            "Timed out waiting for reply to session '{SessionId}'");

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
        : base(conduitOptions, serviceProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <inheritdoc/>
    protected override string EndpointName => this.options.EndpointName;

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
            adminClient, this.options, this.ConduitOptions);

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
            adminClient, this.options, this.ConduitOptions);

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
    protected override async Task StopConsumersAsync(CancellationToken cancellationToken)
    {
        LogStoppingProcessors(this.Logger, null);

        foreach (var processor in this.processors)
        {
            await processor.StopProcessingAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        foreach (var processor in this.processors)
        {
            await processor.DisposeAsync();
        }

        this.processors.Clear();

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

    /// <inheritdoc />
    protected override async Task SendReplyAsync(ProcessMessageEventArgs message, BinaryData replyBody)
    {
        var replyMessage = new ServiceBusMessage(replyBody)
        {
            SessionId = message.Message.ReplyToSessionId,
            ContentType = "application/json",
        };

        await using var sender = this.client!.CreateSender(message.Message.ReplyTo);
        await sender.SendMessageAsync(replyMessage, message.CancellationToken);
    }

    /// <inheritdoc />
    protected override Task AcknowledgeAsync(ProcessMessageEventArgs message) =>
        message.CompleteMessageAsync(message.Message, message.CancellationToken);

    /// <inheritdoc />
    protected override Task AbandonAsync(ProcessMessageEventArgs message) =>
        message.AbandonMessageAsync(message.Message, cancellationToken: message.CancellationToken);

    // -----------------------------------------------------------------------
    // Outbound primitives
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    protected override bool IsStarted => this.topicSender is not null;

    /// <inheritdoc />
    protected override async Task<BinaryData> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        CancellationToken cancellationToken)
    {
        LogSendingRequest(this.Logger, envelope.Type, null);

        var sessionId = Guid.NewGuid().ToString("N");
        var replyQueueName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, "replies", 260);

        var serviceBusMessage = new ServiceBusMessage(body)
        {
            Subject = envelope.Type,
            ContentType = "application/json",
            ReplyToSessionId = sessionId,
            ReplyTo = replyQueueName,
        };

        SeedOutboundMessageContext(serviceBusMessage);

        await this.topicSender!.SendMessageAsync(serviceBusMessage, cancellationToken);

        return await this.AwaitReplyAsync(sessionId, cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task PublishEventAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        CancellationToken cancellationToken)
    {
        LogPublishingEvent(this.Logger, envelope.Type, null);

        var serviceBusMessage = new ServiceBusMessage(body)
        {
            Subject = envelope.Type,
            ContentType = "application/json",
        };

        SeedOutboundMessageContext(serviceBusMessage);

        await this.topicSender!.SendMessageAsync(serviceBusMessage, cancellationToken);
    }

    /// <inheritdoc />
    public override async Task RedeliverAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.client is null)
        {
            throw new InvalidOperationException(
                "Transport has not been started. Ensure the host is running before redelivering messages.");
        }

        var body = new BinaryData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload, ConduitRuntime.JsonOptions));

        var appQueueName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, 260);

        var serviceBusMessage = new ServiceBusMessage(body)
        {
            Subject = envelope.Type,
            ContentType = "application/json",
            MessageId = Guid.NewGuid().ToString("N"),
        };

        // Send directly to the app queue — bypasses topic routing
        await using var sender = this.client.CreateSender(appQueueName);
        await sender.SendMessageAsync(serviceBusMessage, cancellationToken);
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
    protected override string? ExtractTraceHeader(ProcessMessageEventArgs message, string key) =>
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

    private async Task<BinaryData> AwaitReplyAsync(string sessionId, CancellationToken cancellationToken)
    {
        var replyQueueName = TopologyNaming.CreateAppResourceName(this.ConduitOptions, "replies", 260);

        await using var sessionReceiver = await this.client!.AcceptSessionAsync(
            replyQueueName,
            sessionId,
            cancellationToken: cancellationToken);

        var replyMessage = await sessionReceiver.ReceiveMessageAsync(
            this.options.ResponseTimeout, cancellationToken);

        if (replyMessage is null)
        {
            LogReplyTimeout(this.Logger, sessionId, null);
            throw new TimeoutException(
                $"Timed out waiting for reply on session '{sessionId}'. " +
                "The remote handler may have crashed or is taking too long.");
        }

        await sessionReceiver.CompleteMessageAsync(replyMessage, cancellationToken);

        return replyMessage.Body;
    }

    private static void SeedOutboundMessageContext(ServiceBusMessage message)
    {
        var context = MessageContext.Current;
        if (context is not null)
        {
            message.MessageId = context.MessageId;
            message.CorrelationId = context.ConversationId;

            if (context.CausationId is not null)
            {
                message.ApplicationProperties["CausationId"] = context.CausationId;
            }
        }

        if (pendingTraceHeaders is not null)
        {
            foreach (var (key, value) in pendingTraceHeaders)
            {
                message.ApplicationProperties[key] = value;
            }

            pendingTraceHeaders = null;
        }
    }
}
