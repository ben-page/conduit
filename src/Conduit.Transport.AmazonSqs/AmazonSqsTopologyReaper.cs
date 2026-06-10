using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace Conduit.Transport.AmazonSqs;

/// <summary>
/// Deletes SQS queues and SNS topics whose names start with a given prefix. Used to reclaim a test
/// project's resources (named under a stable <c>test-{hash}-</c> prefix) between runs, and available
/// to applications that want their own prefix-scoped cleanup pass. Best-effort: per-resource delete
/// failures are swallowed; the call never throws on a single failure. Returns the number deleted.
/// </summary>
public static class AmazonSqsTopologyReaper
{
    /// <summary>
    /// Deletes all SQS queues and SNS topics whose names start with <paramref name="prefix"/>.
    /// Deleting an SNS topic also removes its subscriptions.
    /// </summary>
    public static async Task<int> ReapByPrefixAsync(
        AmazonSqsOptions options, string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var (snsClient, sqsClient) = AmazonSqsClientFactory.Create(options);
        var reaped = 0;

        using (snsClient)
        using (sqsClient)
        {
            var queues = await sqsClient.ListQueuesAsync(
                new ListQueuesRequest { QueueNamePrefix = prefix, MaxResults = 1000 }, cancellationToken);
            foreach (var queueUrl in queues.QueueUrls ?? [])
            {
                try
                {
                    await sqsClient.DeleteQueueAsync(queueUrl, cancellationToken);
                    reaped++;
                }
                catch (AmazonSQSException)
                {
                    // Best-effort.
                }
            }

            string? nextToken = null;
            do
            {
                var topics = await snsClient.ListTopicsAsync(
                    new ListTopicsRequest { NextToken = nextToken }, cancellationToken);
                foreach (var topic in topics.Topics ?? [])
                {
                    var name = topic.TopicArn[(topic.TopicArn.LastIndexOf(':') + 1)..];
                    if (!name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        await snsClient.DeleteTopicAsync(topic.TopicArn, cancellationToken);
                        reaped++;
                    }
                    catch (AmazonSimpleNotificationServiceException)
                    {
                        // Best-effort.
                    }
                }

                nextToken = topics.NextToken;
            }
            while (!string.IsNullOrEmpty(nextToken));
        }

        return reaped;
    }
}
