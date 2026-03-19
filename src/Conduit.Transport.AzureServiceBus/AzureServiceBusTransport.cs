using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Conduit.Configuration;
using Conduit.Dispatch;
using Conduit.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.AzureServiceBus;

/// <summary>
/// Azure Service Bus transport implementing <see cref="IConduitTransport"/> for outbound
/// messaging and <see cref="IHostedService"/> for background receive lifecycle.
/// </summary>
/// <remarks>
/// <para>Outbound: <see cref="SendRequestAsync"/> sends commands/queries to endpoint queues
/// and awaits replies via ASB sessions. <see cref="SendEventAsync"/> publishes events to the
/// shared topic.</para>
/// <para>Inbound: Background processors listen on the app queue (and pool queues), create
/// DI scopes per message, resolve <see cref="IWireMediator"/>, seed
/// <see cref="MessageContext"/>, and dispatch through the mediator pipeline.</para>
/// </remarks>
public sealed class AzureServiceBusTransport : IConduitTransport, IHostedService, IAsyncDisposable
{
    private readonly AzureServiceBusOptions options;
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<AzureServiceBusTransport> logger;
    private readonly List<ServiceBusProcessor> processors = [];

    private ServiceBusClient? client;
    private ServiceBusSender? topicSender;
    private ConduitOptions? conduitOptions;

    // LoggerMessage delegates
    private static readonly Action<ILogger, string, string, Exception?> LogSendingRequest =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(100, "SendingRequest"),
            "Sending request '{Type}' to queue '{Queue}'");

    private static readonly Action<ILogger, string, Exception?> LogPublishingEvent =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(101, "PublishingEvent"),
            "Publishing event '{Type}' to topic");

    private static readonly Action<ILogger, string, Exception?> LogReceivedMessage =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(102, "ReceivedMessage"),
            "Received message '{Type}' from queue");

    private static readonly Action<ILogger, string, Exception?> LogMessageCompleted =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(103, "MessageCompleted"),
            "Completed message '{Type}'");

    private static readonly Action<ILogger, string, Exception?> LogMessageAbandoned =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(104, "MessageAbandoned"),
            "Abandoned event message '{Type}' due to listener failure — broker will redeliver");

    private static readonly Action<ILogger, string, string, Exception?> LogProcessingError =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(105, "ProcessingError"),
            "Error processing message on '{EntityPath}': {ErrorSource}");

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

    private static readonly Action<ILogger, Exception?> LogSkippingNoSubject =
        LoggerMessage.Define(LogLevel.Warning, new EventId(110, "SkippingNoSubject"),
            "Received message with no Subject (type discriminator) — completing and skipping");

    private static readonly Action<ILogger, string, Exception?> LogAutoCreateTopologyOverride =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(111, "AutoCreateTopologyOverride"),
            "AutoCreateTopology was false but TopologyMode is '{TopologyMode}' — overriding to true");

    /// <summary>
    /// Creates a new Azure Service Bus transport.
    /// </summary>
    /// <param name="options">Transport configuration.</param>
    /// <param name="serviceProvider">The application service provider for resolving services at runtime.</param>
    /// <param name="logger">Logger for transport diagnostics.</param>
    public AzureServiceBusTransport(
        AzureServiceBusOptions options,
        IServiceProvider serviceProvider,
        ILogger<AzureServiceBusTransport> logger)
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

        if (this.client is null)
        {
            throw new InvalidOperationException(
                "Transport has not been started. Ensure the host is running before sending messages.");
        }

        // Publish to the topic — subscriptions route to the correct handler's queue.
        var topicName = TopologyNaming.CreateSharedResourceName(this.conduitOptions!, 260);

        LogSendingRequest(this.logger, envelope.Type, topicName, null);

        var sessionId = Guid.NewGuid().ToString("N");
        var messageBody = JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload, ConduitRuntime.JsonOptions);

        var replyQueueName = TopologyNaming.CreateAppResourceName(this.conduitOptions!, "replies", 260);
        var serviceBusMessage = new ServiceBusMessage(messageBody)
        {
            Subject = envelope.Type,
            ContentType = "application/json",
            ReplyToSessionId = sessionId,
            ReplyTo = replyQueueName,
        };

        SeedOutboundMessageContext(serviceBusMessage);

        await using var sender = this.client.CreateSender(topicName);
        await sender.SendMessageAsync(serviceBusMessage, cancellationToken);

        // Await reply on the session-enabled reply queue
        return await this.AwaitReplyAsync(sessionId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (this.topicSender is null)
        {
            throw new InvalidOperationException(
                "Transport has not been started. Ensure the host is running before publishing events.");
        }

        LogPublishingEvent(this.logger, envelope.Type, null);

        var messageBody = JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload, ConduitRuntime.JsonOptions);

        var serviceBusMessage = new ServiceBusMessage(messageBody)
        {
            Subject = envelope.Type,
            ContentType = "application/json",
        };

        SeedOutboundMessageContext(serviceBusMessage);

        await this.topicSender.SendMessageAsync(serviceBusMessage, cancellationToken);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        this.conduitOptions = this.ResolveConduitOptions();

        var topicName = TopologyNaming.CreateSharedResourceName(this.conduitOptions, 260);
        this.client = new ServiceBusClient(this.options.ConnectionString);
        this.topicSender = this.client.CreateSender(topicName);

        var adminConnectionString = this.options.AdminConnectionString ?? this.options.ConnectionString;
        var adminClient = new ServiceBusAdministrationClient(adminConnectionString);
        var topologyManager = new AzureServiceBusTopologyManager(
            adminClient, this.options, this.conduitOptions);

        // Get registered types from compile-time registry
        var registry = this.serviceProvider.GetRequiredService<ConduitTypeRegistry>();
        var subscribedEventTypes = registry.GetEventListenerNames();
        var handledCommandQueryTypes = registry.GetCommandHandlerNames()
            .Concat(registry.GetQueryHandlerNames()).ToList();
        LogDiscoveredEventTypes(this.logger, subscribedEventTypes.Count, null);

        // Non-production modes force AutoCreateTopology on
        var autoCreate = this.options.AutoCreateTopology;
        if (!autoCreate && this.conduitOptions.TopologyMode != TopologyMode.Production)
        {
            autoCreate = true;
            LogAutoCreateTopologyOverride(
                this.logger, this.conduitOptions.TopologyMode.ToString(), null);
        }

        // Create topology if enabled
        if (autoCreate)
        {
            await topologyManager.EnsureTopologyAsync(
                subscribedEventTypes, handledCommandQueryTypes, cancellationToken);
        }

        // Start processors
        var appQueueName = TopologyNaming.CreateAppResourceName(this.conduitOptions, 260);
        await this.StartProcessorAsync(appQueueName, this.options.DefaultConcurrency, cancellationToken);

        foreach (var pool in this.options.Pools)
        {
            var poolQueueName = TopologyNaming.CreateAppResourceName(
                this.conduitOptions, pool.Name, 260);
            await this.StartProcessorAsync(poolQueueName, pool.Concurrency, cancellationToken);
        }

        LogStartingProcessors(this.logger, this.processors.Count, null);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        LogStoppingProcessors(this.logger, null);

        foreach (var processor in this.processors)
        {
            await processor.StopProcessingAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
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

    /// <summary>
    /// Creates and starts a ServiceBusProcessor for the given queue.
    /// </summary>
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
        processor.ProcessMessageAsync += this.ProcessMessageAsync;
        processor.ProcessErrorAsync += this.ProcessErrorAsync;

        await processor.StartProcessingAsync(cancellationToken);
        this.processors.Add(processor);
    }

    /// <summary>
    /// Processes a single inbound message: creates DI scope, resolves IWireMediator,
    /// seeds MessageContext, dispatches, and completes/abandons the message.
    /// </summary>
    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var messageType = args.Message.Subject;
        if (string.IsNullOrWhiteSpace(messageType))
        {
            LogSkippingNoSubject(this.logger, null);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
            return;
        }

        LogReceivedMessage(this.logger, messageType, null);

        // Seed MessageContext from inbound message properties
        var previousContext = MessageContext.Current;
        try
        {
            var inboundMessageId = args.Message.MessageId ?? Guid.NewGuid().ToString("N");
            var inboundConversationId = args.Message.CorrelationId ?? inboundMessageId;
            args.Message.ApplicationProperties.TryGetValue("CausationId", out var causationIdObj);
            var inboundCausationId = causationIdObj as string;

            MessageContext.StartDispatchFromTransport(
                inboundMessageId, inboundCausationId, inboundConversationId);

            await using var scope = this.serviceProvider!.CreateAsyncScope();
            var wireMediator = scope.ServiceProvider.GetRequiredService<IWireMediator>();

            var payload = JsonSerializer.Deserialize<JsonElement>(
                args.Message.Body.ToArray(), ConduitRuntime.JsonOptions);
            var envelope = new DispatchEnvelope(messageType, payload);

            // Check if this is a command/query (has ReplyToSessionId) or an event
            if (!string.IsNullOrWhiteSpace(args.Message.ReplyToSessionId))
            {
                // Request/response: always complete, send reply
                var result = await wireMediator.HandleWireRequestAsync(envelope, args.CancellationToken);

                await this.SendReplyAsync(
                    args.Message.ReplyTo,
                    args.Message.ReplyToSessionId,
                    result,
                    args.CancellationToken);

                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                LogMessageCompleted(this.logger, messageType, null);
            }
            else
            {
                // Event: complete on success, abandon on EventListenerException
                try
                {
                    await wireMediator.HandleWireEventAsync(envelope, args.CancellationToken);
                    await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                    LogMessageCompleted(this.logger, messageType, null);
                }
                catch (EventListenerException ex)
                {
                    LogMessageAbandoned(this.logger, messageType, ex);
                    await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
                }
            }
        }
        finally
        {
            MessageContext.RestoreContext(previousContext);
        }
    }

    /// <summary>
    /// Handles processor errors (connection issues, etc.).
    /// </summary>
    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        LogProcessingError(this.logger, args.EntityPath, args.ErrorSource.ToString(), args.Exception);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends a reply message to the session-enabled reply queue.
    /// </summary>
    private async Task SendReplyAsync(
        string replyTo,
        string sessionId,
        DispatchResult<object> result,
        CancellationToken cancellationToken)
    {
        var replyBody = JsonSerializer.SerializeToUtf8Bytes(result, ConduitRuntime.JsonOptions);
        var replyMessage = new ServiceBusMessage(replyBody)
        {
            SessionId = sessionId,
            ContentType = "application/json",
        };

        await using var sender = this.client!.CreateSender(replyTo);
        await sender.SendMessageAsync(replyMessage, cancellationToken);
    }

    /// <summary>
    /// Awaits a reply on the session-enabled reply queue by accepting a session
    /// with the given session ID and reading the response.
    /// </summary>
    private async Task<object?> AwaitReplyAsync(string sessionId, CancellationToken cancellationToken)
    {
        var replyQueueName = TopologyNaming.CreateAppResourceName(this.conduitOptions!, "replies", 260);

        await using var sessionReceiver = await this.client!.AcceptSessionAsync(
            replyQueueName,
            sessionId,
            cancellationToken: cancellationToken);

        var replyMessage = await sessionReceiver.ReceiveMessageAsync(
            TimeSpan.FromSeconds(30), cancellationToken);

        if (replyMessage is null)
        {
            LogReplyTimeout(this.logger, sessionId, null);
            throw new TimeoutException(
                $"Timed out waiting for reply on session '{sessionId}'. " +
                "The remote handler may have crashed or is taking too long.");
        }

        await sessionReceiver.CompleteMessageAsync(replyMessage, cancellationToken);

        var result = JsonSerializer.Deserialize<DispatchResult<JsonElement?>>(
            replyMessage.Body.ToArray(), ConduitRuntime.JsonOptions);

        if (result?.Error is not null)
        {
            throw new InvalidOperationException(
                $"Remote dispatch failed: [{result.Error.Code}] {result.Error.Message}");
        }

        return result?.Data;
    }

    /// <summary>
    /// Seeds outbound message properties from the current <see cref="MessageContext"/>.
    /// </summary>
    private static void SeedOutboundMessageContext(ServiceBusMessage message)
    {
        var context = MessageContext.Current;
        if (context is null)
        {
            return;
        }

        message.MessageId = context.MessageId;
        message.CorrelationId = context.ConversationId;

        if (context.CausationId is not null)
        {
            message.ApplicationProperties["CausationId"] = context.CausationId;
        }
    }
}
