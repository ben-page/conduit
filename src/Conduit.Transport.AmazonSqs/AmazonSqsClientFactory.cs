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
    public static (AmazonSimpleNotificationServiceClient Sns, AmazonSQSClient Sqs) Create(AmazonSqsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ServiceUri is not null)
        {
            // LocalStack or custom endpoint — use explicit credentials
            // (LocalStack doesn't validate them but the SDK requires a credential source).
            var serviceUrl = options.ServiceUri.ToString();
            var credentials = options.Credentials ?? new BasicAWSCredentials("test", "test");

            var sns = new AmazonSimpleNotificationServiceClient(
                credentials, new AmazonSimpleNotificationServiceConfig { ServiceURL = serviceUrl });
            var sqs = new AmazonSQSClient(
                credentials, new AmazonSQSConfig { ServiceURL = serviceUrl });
            return (sns, sqs);
        }

        if (options.Region is not null)
        {
            return (new AmazonSimpleNotificationServiceClient(options.Region),
                new AmazonSQSClient(options.Region));
        }

        // Default credential chain + default region.
        return (new AmazonSimpleNotificationServiceClient(), new AmazonSQSClient());
    }
}
