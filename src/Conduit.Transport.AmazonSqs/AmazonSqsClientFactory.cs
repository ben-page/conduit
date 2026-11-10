using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

namespace Conduit.Transport.AmazonSqs;

/// <summary>
/// Builds the SNS and SQS clients from <see cref="AmazonSqsOptions"/>. Single source of truth for
/// client construction, shared by <see cref="AmazonSqsTransport"/> and <see cref="AmazonSqsTopologyReaper"/>.
/// </summary>
internal static class AmazonSqsClientFactory
{
    /// <summary>
    /// The two clients. <see cref="AmazonSqsOptions.Credentials"/> applies whenever it is set, with or without
    /// <see cref="AmazonSqsOptions.ServiceUri"/>, which only overrides the endpoint (and then
    /// <see cref="AmazonSqsOptions.Region"/> is not used). With no credentials, a custom endpoint gets a
    /// placeholder pair (LocalStack validates none, but the SDK insists on a credential source) and AWS itself
    /// the SDK's default credential chain. The credentials go on each client's configuration
    /// (<see cref="ClientConfig.DefaultAWSCredentials"/>), which the SDK signs with when the client was
    /// constructed without explicit ones, so they stay readable from the client.
    /// </summary>
    public static (AmazonSimpleNotificationServiceClient Sns, AmazonSQSClient Sqs) Create(AmazonSqsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var credentials = options.Credentials
            ?? (options.ServiceUri is not null ? new BasicAWSCredentials("test", "test") : null);

        var snsConfig = new AmazonSimpleNotificationServiceConfig { DefaultAWSCredentials = credentials };
        var sqsConfig = new AmazonSQSConfig { DefaultAWSCredentials = credentials };

        if (options.ServiceUri is not null)
        {
            // LocalStack or another custom endpoint
            snsConfig.ServiceURL = options.ServiceUri.ToString();
            sqsConfig.ServiceURL = options.ServiceUri.ToString();
        }
        else if (options.Region is not null)
        {
            snsConfig.RegionEndpoint = options.Region;
            sqsConfig.RegionEndpoint = options.Region;
        }

        // Neither set: the SDK resolves the region itself, as it does for a client built with no arguments
        return (new AmazonSimpleNotificationServiceClient(snsConfig), new AmazonSQSClient(sqsConfig));
    }
}
