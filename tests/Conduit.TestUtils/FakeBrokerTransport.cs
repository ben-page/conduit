using System.Collections.Concurrent;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.TestUtils;

/// <summary>
/// In-memory <see cref="BrokerTransportBase{TMessage}"/> for testing base-class behavior
/// (outbound header assembly, inbound dispatch, self-filtering) without a real broker.
/// </summary>
public sealed class FakeBrokerTransport : BrokerTransportBase<FakeBrokerMessage>
{
    private readonly ConcurrentQueue<FakeBrokerMessage> publishedEvents = new();
    private readonly ConcurrentQueue<BinaryData> replies = new();
    private readonly ConcurrentQueue<string> lifecycle = new();

    // What a publish that ignores its token waits on: never set
    private readonly TaskCompletionSource ignoredPublish = new();
    private volatile bool started;

    public FakeBrokerTransport(
        ConduitOptions conduitOptions,
        IServiceProvider serviceProvider,
        FakeBrokerOptions? options = null,
        ILogger? logger = null)
        : base(options ?? new FakeBrokerOptions(), conduitOptions, serviceProvider, logger ?? NullLogger.Instance)
    {
    }

    /// <summary>
    /// When set, a request's publish never completes until its token is cancelled, as a publish whose
    /// broker confirm never comes (a broker under a memory alarm stops reading the connection).
    /// </summary>
    public bool PublishRequestsHang { get; set; }

    /// <summary>
    /// When set, a request's publish never completes and ignores its token, as a broker client stuck in a
    /// call that takes no cancellation.
    /// </summary>
    public bool PublishRequestsIgnoreTheirToken { get; set; }

    /// <summary>Events handed to the "broker", in publish order.</summary>
    public IReadOnlyList<FakeBrokerMessage> PublishedEvents => this.publishedEvents.ToArray();

    /// <summary>Replies sent back to requesters, in send order.</summary>
    public IReadOnlyList<BinaryData> Replies => this.replies.ToArray();

    /// <summary>
    /// The stop sequence as the "broker" saw it, in order: <c>receiving stopped</c>, each
    /// <c>acknowledged</c>, <c>consumers stopped</c>.
    /// </summary>
    public IReadOnlyList<string> Lifecycle => this.lifecycle.ToArray();

    /// <summary>Marks the transport started so the send guard passes.</summary>
    public void Start() => this.started = true;

    /// <summary>Feeds a message through the inbound pipeline, as a consumer would.</summary>
    public Task DeliverAsync(FakeBrokerMessage message) => this.ProcessInboundMessageAsync(message);

    protected override string TransportName => "fake";

    protected override bool IsStarted => this.started;

    protected override Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task EnsureTopologyAsync(
        IReadOnlyList<string> eventTypeNames,
        IReadOnlyList<string> commandQueryTypeNames,
        CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task EnsureOutboundTopologyAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected override Task StartConsumersAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task StopReceivingAsync(CancellationToken cancellationToken)
    {
        this.lifecycle.Enqueue("receiving stopped");
        return Task.CompletedTask;
    }

    protected override Task StopConsumersAsync(CancellationToken cancellationToken)
    {
        this.lifecycle.Enqueue("consumers stopped");
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected override string? GetMessageType(FakeBrokerMessage message) => message.Type;

    protected override BinaryData GetMessageBody(FakeBrokerMessage message) => message.Body;

    protected override bool IsRequest(FakeBrokerMessage message) => message.IsRequest;

    protected override (string MessageId, string? CausationId, string ConversationId) ExtractContext(
        FakeBrokerMessage message)
    {
        var messageId = message.Properties.GetValueOrDefault("MessageId") ?? Guid.NewGuid().ToString("N");
        var conversationId = message.Properties.GetValueOrDefault("ConversationId") ?? messageId;
        return (messageId, message.Properties.GetValueOrDefault("CausationId"), conversationId);
    }

    protected override Task SendReplyAsync(FakeBrokerMessage message, BinaryData replyBody)
    {
        this.replies.Enqueue(replyBody);
        return Task.CompletedTask;
    }

    protected override Task AcknowledgeAsync(FakeBrokerMessage message)
    {
        message.AcknowledgeAttempts++;
        if (message.AcknowledgeFailure is { } failure)
        {
            throw failure;
        }

        message.Acknowledged = true;
        this.lifecycle.Enqueue("acknowledged");
        return Task.CompletedTask;
    }

    protected override Task AbandonAsync(FakeBrokerMessage message)
    {
        message.Abandoned = true;
        return Task.CompletedTask;
    }

    protected override Task RequeueAsync(FakeBrokerMessage message)
    {
        message.Requeued = true;
        return Task.CompletedTask;
    }

    protected override async Task<ReadOnlyMemory<byte>> PublishRequestAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        IReadOnlyDictionary<string, string> headers,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (this.PublishRequestsHang)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        if (this.PublishRequestsIgnoreTheirToken)
        {
            await this.ignoredPublish.Task;
        }

        return new BinaryData("{}").ToMemory();
    }

    protected override Task PublishEventAsync(
        DispatchEnvelope envelope,
        BinaryData body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        this.publishedEvents.Enqueue(new FakeBrokerMessage
        {
            Type = envelope.Type,
            Body = body,
            Properties = new Dictionary<string, string>(headers, StringComparer.Ordinal),
        });
        return Task.CompletedTask;
    }

    protected override string? ExtractMessageHeader(FakeBrokerMessage message, string key)
        => message.Properties.GetValueOrDefault(key);
}
