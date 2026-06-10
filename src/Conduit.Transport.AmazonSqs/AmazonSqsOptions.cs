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
    /// Timeout for request/response operations. Default 60 seconds.
    /// Must be less than or equal to <see cref="VisibilityTimeoutSeconds"/>.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(60);

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
    /// The endpoint name this transport is registered under in keyed DI.
    /// Set automatically by <c>UseAmazonSqs()</c> from the <c>EndpointBuilder</c> context.
    /// Used by dead letter queue capture to record the source endpoint and by replay to
    /// resolve the correct transport.
    /// </summary>
    public string EndpointName { get; internal set; } = string.Empty;
}
