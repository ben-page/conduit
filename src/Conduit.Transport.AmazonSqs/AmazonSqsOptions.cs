using Amazon;

namespace Conduit.Transport.AmazonSqs;

/// <summary>
/// Configuration for the Amazon SQS (+SNS) transport.
/// Authentication uses the default AWS credential chain (environment, profile, or IAM role).
/// </summary>
public sealed class AmazonSqsOptions
{
    /// <summary>
    /// AWS region for the SNS/SQS clients. Required unless <see cref="ServiceUri"/> is set.
    /// </summary>
    public RegionEndpoint? Region { get; set; }

    /// <summary>
    /// Custom service URI for LocalStack or other local development environments.
    /// When set, overrides the endpoint for both SNS and SQS clients.
    /// Example: <c>new Uri("http://localhost:4566")</c>
    /// </summary>
    public Uri? ServiceUri { get; set; }

    /// <summary>
    /// Timeout for request/response operations. Default 60 seconds.
    /// Must be less than or equal to <see cref="VisibilityTimeoutSeconds"/>.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum concurrent message processing tasks for the default consumer.
    /// </summary>
    public int DefaultMaxConcurrency { get; set; } = 10;

    /// <summary>
    /// SQS visibility timeout in seconds. Messages become invisible during processing.
    /// Must be greater than or equal to <see cref="ResponseTimeout"/> total seconds.
    /// Default 60 seconds.
    /// </summary>
    public int VisibilityTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Whether raw message delivery is enabled on SNS subscriptions.
    /// Must be <c>true</c> for filter policies to work correctly.
    /// The transport validates this at startup and throws if <c>false</c>.
    /// </summary>
    public bool RawMessageDelivery { get; set; } = true;

    /// <summary>
    /// Named receiver pools for concurrency isolation.
    /// Each pool gets its own SQS queue and polling loop.
    /// </summary>
    public List<AmazonSqsReceiverPoolOptions> Pools { get; } = [];

    /// <summary>
    /// Adds a named receiver pool with custom concurrency and message type routing.
    /// </summary>
    /// <param name="name">The pool name (used in queue naming).</param>
    /// <param name="configure">Callback to configure the pool.</param>
    public void AddPool(string name, Action<AmazonSqsReceiverPoolOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var pool = new AmazonSqsReceiverPoolOptions { Name = name };
        configure(pool);
        this.Pools.Add(pool);
    }
}
