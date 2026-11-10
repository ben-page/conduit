using Amqp;
using Amqp.Framing;

namespace Conduit.Transport.ActiveMQ;

/// <summary>
/// One AMQP connection to Artemis and everything the transport holds on it: the session, the command
/// and event sender links, the reply receiver and the address the broker assigned it, the consumer
/// links, and the reply senders. AMQPNetLite never reconnects, so when the connection is lost the
/// transport discards the whole object and builds a new one.
/// </summary>
/// <remarks>
/// The callback the transport passes runs once, on an AMQPNetLite thread, the first time the
/// connection, the session, a sender, the reply receiver or a consumer link closes without this object
/// closing it: a broker restart closes them all, and any one of them gone leaves the transport unable to
/// send, receive or get replies. A reply sender is not watched: one closes whenever the requester whose
/// reply queue it targets goes away.
/// </remarks>
internal sealed class ActiveMQConnection : IAsyncDisposable
{
    // Replies are settled the moment they arrive (no handler runs), so the reply link's credit only lets
    // the broker pipeline them; it never bounds how many requests can wait for a reply at once.
    private const int ReplyCredit = 200;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly Connection connection;
    private readonly Action<ActiveMQConnection> onLost;
    private readonly Lock consumerLinksLock = new();
    private readonly List<ReceiverLink> consumerLinks = [];
    private volatile string? replyAddress;
    private Loss? loss;
    private int lossReported;
    private int closing;

    private ActiveMQConnection(Connection connection, Action<ActiveMQConnection> onLost)
    {
        this.connection = connection;
        this.onLost = onLost;
        this.Session = new Session(connection);
        this.ReplySenders = new ActiveMQReplySenders(this.Session);

        this.Watch(connection);
        this.Watch(this.Session);
    }

    /// <summary>The session every link of this connection is on.</summary>
    public Session Session { get; }

    /// <summary>The cached sender links that carry replies, one per reply address.</summary>
    public ActiveMQReplySenders ReplySenders { get; }

    /// <summary>The sender link to the anycast address, once attached.</summary>
    public SenderLink? CommandSender { get; private set; }

    /// <summary>The sender link to the multicast address, once attached.</summary>
    public SenderLink? EventSender { get; private set; }

    /// <summary>
    /// The address of this connection's temporary reply queue, once the broker assigned it: set last, so
    /// a thread that sees it sees the sender links too.
    /// </summary>
    public string? ReplyAddress => this.replyAddress;

    /// <summary>Whether something this connection holds closed without this object closing it.</summary>
    public bool IsLost => Volatile.Read(ref this.loss) is not null;

    /// <summary>The AMQP error the first unexpected close carried, if it carried one.</summary>
    public Error? LostError => Volatile.Read(ref this.loss)?.Error;

    /// <summary>
    /// True the first time it is called for a lost connection: whoever gets true reports the loss, so it
    /// is reported once, whether the close's callback or the end of the transport's start sees it first.
    /// </summary>
    public bool TryClaimLossReport() => this.IsLost && Interlocked.Exchange(ref this.lossReported, 1) == 0;

    /// <summary>Whether the connection can send requests and events and receive replies.</summary>
    public bool IsReady =>
        this.CommandSender is not null && this.EventSender is not null && this.ReplyAddress is not null && !this.IsLost;

    /// <summary>
    /// Opens a connection to the broker <paramref name="options"/> name, giving up after 30 seconds or when
    /// <paramref name="cancellationToken"/> is cancelled, and begins its session.
    /// </summary>
    /// <param name="options">The transport's options: the connection string and credentials.</param>
    /// <param name="onLost">Called once when the connection is lost; see the remarks on the class.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <exception cref="TimeoutException">The broker did not open the connection within 30 seconds.</exception>
    public static async Task<ActiveMQConnection> OpenAsync(
        ActiveMQOptions options,
        Action<ActiveMQConnection> onLost,
        CancellationToken cancellationToken)
    {
        var address = new Address(options.ConnectionString);
        if (options.Username is not null)
        {
            address = new Address(
                address.Host,
                address.Port,
                options.Username,
                options.Password,
                address.Path,
                address.Scheme);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        // The token reaches the TCP connect and the AMQP open but not the SASL exchange between them, so
        // WaitAsync bounds the whole attempt, and a connection that still opens after it gave up is closed
        var opening = new ConnectionFactory().CreateAsync(address, timeout.Token);
        Connection opened;
        try
        {
            opened = await opening.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CloseIfItOpensLate(opening);
            throw new TimeoutException(
                $"Could not connect to Artemis at {address.Host}:{address.Port} within {ConnectTimeout.TotalSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            CloseIfItOpensLate(opening);
            throw;
        }

        return new ActiveMQConnection(opened, onLost);
    }

    /// <summary>
    /// Attaches the sender links to the two addresses and the reply receiver, a dynamic node the broker
    /// names, whose replies go to <paramref name="onReply"/>. Waits at most 30 seconds for the broker to
    /// name it, or until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task AttachSendersAsync(
        string anycastAddress,
        string multicastAddress,
        MessageCallback onReply,
        CancellationToken cancellationToken)
    {
        this.CommandSender = this.Watch(new SenderLink(
            this.Session, ActiveMQTopologyManager.LinkName("command-sender"), anycastAddress));
        this.EventSender = this.Watch(new SenderLink(
            this.Session, ActiveMQTopologyManager.LinkName("event-sender"), multicastAddress));

        // An exclusive temporary reply queue: the broker assigns its address in the attach, and deletes
        // it with the link
        var attached = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replyReceiver = this.Watch(new ReceiverLink(
            this.Session,
            ActiveMQTopologyManager.LinkName("reply-receiver"),
            new Source { Dynamic = true },
            (link, attach) =>
            {
                if (attach.Source is Source source && source.Address is not null)
                {
                    attached.TrySetResult(source.Address);
                }
                else
                {
                    attached.TrySetException(
                        new InvalidOperationException("Broker did not assign a dynamic reply queue address."));
                }
            }));

        var assignedAddress = await attached.Task.WaitAsync(AttachTimeout, cancellationToken);
        replyReceiver.Start(ReplyCredit, onReply);
        this.replyAddress = assignedAddress;
    }

    /// <summary>
    /// Attaches a consumer link to the queue named by <paramref name="fullyQualifiedQueueName"/>
    /// (<c>address::queue</c>) with <paramref name="credit"/> as its prefetch, handing each message to
    /// <paramref name="onMessage"/>.
    /// </summary>
    public void StartConsumer(string fullyQualifiedQueueName, int credit, MessageCallback onMessage)
    {
        var receiverLink = this.Watch(new ReceiverLink(
            this.Session,
            ActiveMQTopologyManager.ConsumerLinkName(),
            new Source { Address = fullyQualifiedQueueName },
            null));

        lock (this.consumerLinksLock)
        {
            this.consumerLinks.Add(receiverLink);
        }

        receiverLink.Start(credit, onMessage);
    }

    /// <summary>
    /// Stops replenishing each consumer link's credit, keeping the links open so a handler still running
    /// settles its message on the link it arrived on. AMQPNetLite sends no flow frame for it: the broker
    /// may still push up to the credit each link has outstanding (at most its prefetch), and the base
    /// class hands each such message back. A real withdrawal is not available: AMQPNetLite's drain mode
    /// zeroes the link's local credit at once, and a transfer already in flight under the old credit then
    /// fails with <c>transfer-limit-exceeded</c>, which closes the whole connection.
    /// </summary>
    public void StopReplenishingConsumerCredit()
    {
        foreach (var receiverLink in this.SnapshotConsumerLinks())
        {
            try
            {
                if (!receiverLink.IsClosed)
                {
                    receiverLink.SetCredit(0, CreditMode.Auto);
                }
            }
#pragma warning disable CA1031 // Best effort while stopping: a link whose credit cannot be set is closed with the others anyway.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best effort: a link that failed is closed with the others anyway
            }
        }
    }

    /// <summary>
    /// Starts closing every consumer link; the broker releases whatever is still unsettled on one, so it
    /// is redelivered. From here on a closure is this object's own, never a lost connection.
    /// </summary>
    /// <returns>The close operations, already started; each one completes and never throws.</returns>
    public IReadOnlyList<Task> CloseConsumerLinks()
    {
        Volatile.Write(ref this.closing, 1);

        return [.. this.SnapshotConsumerLinks()
            .Where(receiverLink => !receiverLink.IsClosed)
            .Select(CloseQuietlyAsync)];
    }

    /// <summary>
    /// Closes the connection without waiting for the broker, failing at once whatever is in progress on
    /// it: used when stop overtakes a reconnection still building this connection.
    /// </summary>
    public void Abort()
    {
        Volatile.Write(ref this.closing, 1);

        try
        {
            this.connection.Close(TimeSpan.Zero);
        }
#pragma warning disable CA1031 // Abort never throws: a connection already closing or closed may refuse the close.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Already closing or closed
        }
    }

    /// <summary>
    /// Closes the connection, and with it the session and every link: the broker deletes the temporary
    /// reply queue and releases anything still unsettled. Waits at most 5 seconds; never throws.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref this.closing, 1);

        try
        {
            if (!this.connection.IsClosed)
            {
                await this.connection.CloseAsync(CloseTimeout);
            }
        }
#pragma warning disable CA1031 // Disposal never throws: a lost connection is already closed, and one that does not answer is abandoned.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Best-effort: a lost connection is already closed, and one that does not answer is abandoned
        }
    }

    private static async Task CloseQuietlyAsync(ReceiverLink receiverLink)
    {
        try
        {
            await receiverLink.CloseAsync(CloseTimeout);
        }
#pragma warning disable CA1031 // Closing never throws: the broker releases whatever is unsettled on the link either way.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Best-effort cleanup during shutdown
        }
    }

    private static void CloseIfItOpensLate(Task<Connection> opening) =>
        _ = opening.ContinueWith(
            static attempt =>
            {
                if (!attempt.IsCompletedSuccessfully)
                {
                    _ = attempt.Exception;
                    return;
                }

                try
                {
                    attempt.Result.Close(TimeSpan.Zero);
                }
#pragma warning disable CA1031 // A connection nothing else holds is closed best effort, on a continuation no one observes.
                catch (Exception)
#pragma warning restore CA1031
                {
                    // Nothing else holds it
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private List<ReceiverLink> SnapshotConsumerLinks()
    {
        lock (this.consumerLinksLock)
        {
            return [.. this.consumerLinks];
        }
    }

    private T Watch<T>(T amqpObject)
        where T : AmqpObject
    {
        amqpObject.AddClosedCallback((closed, error) =>
        {
            // The loss and its error are set together, before the callback, so a reader that sees the
            // connection lost also sees why
            if (Volatile.Read(ref this.closing) == 0
                && Interlocked.CompareExchange(ref this.loss, new Loss(error), null) is null)
            {
                this.onLost(this);
            }
        });

        return amqpObject;
    }

    // The first unexpected close, with the AMQP error it carried
    private sealed record Loss(Error? Error);
}
