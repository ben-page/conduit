using Amazon;
using Amazon.Runtime;
using Conduit.Runtime;

namespace Conduit.Transport.AmazonSqs;

/// <summary>
/// Configuration for the Amazon SQS (+SNS) transport.
/// Authentication uses the default AWS credential chain (environment, profile, or IAM role).
/// </summary>
/// <remarks>
/// <see cref="BrokerTransportOptions.DefaultConcurrency"/> and
/// <see cref="ReceiverPoolOptions.Concurrency"/> map to the polling parallelism bound
/// (the per-consumer <c>SemaphoreSlim</c> limit).
/// </remarks>
public sealed class AmazonSqsOptions : BrokerTransportOptions
{
    /// <summary>Creates options with every setting at its default.</summary>
    public AmazonSqsOptions()
    {
    }

    /// <summary>Creates options for the endpoint <c>UseAmazonSqs()</c> is configuring.</summary>
    internal AmazonSqsOptions(string endpointName) => this.EndpointName = endpointName;

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
    /// Explicit AWS credentials. When null and <see cref="ServiceUri"/> is set,
    /// dummy credentials are used automatically (LocalStack doesn't validate them).
    /// When null and ServiceUri is not set, the default AWS credential chain is used.
    /// </summary>
    public AWSCredentials? Credentials { get; set; }

    /// <summary>
    /// SQS visibility timeout in seconds. Messages become invisible during processing.
    /// Must be greater than or equal to <see cref="BrokerTransportOptions.ResponseTimeout"/> total
    /// seconds, so a request is not redelivered while its caller still awaits the reply.
    /// Default 60 seconds.
    /// </summary>
    public int VisibilityTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// How many times SQS delivers a message from the application's queue, or a pool's, before moving it to
    /// that queue's dead-letter queue (<c>{queue}-dlq</c>): the <c>maxReceiveCount</c> of the redrive policy
    /// the transport sets on each. A failed message that no Conduit dead-letter store captured is delivered
    /// again once its visibility timeout lapses, so a listener that always fails runs this many times, a
    /// visibility timeout apart. Every delivery counts, one handed back undispatched on shutdown included.
    /// Between 1 and 1000. Default 5.
    /// </summary>
    public int MaxReceiveCount { get; set; } = 5;

    /// <summary>
    /// Whether raw message delivery is enabled on SNS subscriptions.
    /// Must be <c>true</c> for filter policies to work correctly.
    /// The transport validates this at startup and throws if <c>false</c>.
    /// </summary>
    public bool RawMessageDelivery { get; set; } = true;
}
