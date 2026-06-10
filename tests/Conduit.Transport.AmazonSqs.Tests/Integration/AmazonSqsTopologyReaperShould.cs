using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using AwesomeAssertions;
using Conduit.Transport.AmazonSqs;

namespace Conduit.Transport.AmazonSqs.Tests.Integration;

/// <summary>
/// Transport test matrix 7.4 (CleanupStaleResources) for SQS: <see cref="AmazonSqsTopologyReaper"/>
/// deletes queues and SNS topics whose names start with a given prefix and leaves all others intact.
/// </summary>
[TestClass]
public sealed class AmazonSqsTopologyReaperShould : SqsTestBase
{
    [TestMethod]
    public async Task ReapQueuesAndTopicsMatchingThePrefixAndLeaveOthers()
    {
        // Arrange
        var credentials = new BasicAWSCredentials("test", "test");
        using var sqsClient = new AmazonSQSClient(credentials,
            new AmazonSQSConfig { ServiceURL = ServiceAddress });
        using var snsClient = new AmazonSimpleNotificationServiceClient(credentials,
            new AmazonSimpleNotificationServiceConfig { ServiceURL = ServiceAddress });

        var reapPrefix = $"test-reap{Guid.NewGuid():N}"[..16] + "-";
        var matchingQueue = $"{reapPrefix}conduit-deadrun";
        var matchingTopic = $"{reapPrefix}conduit";
        var otherQueue = $"test-keep{Guid.NewGuid():N}"[..16] + "-conduit-survivor";

        await sqsClient.CreateQueueAsync(new CreateQueueRequest { QueueName = matchingQueue });
        await snsClient.CreateTopicAsync(new CreateTopicRequest { Name = matchingTopic });
        await sqsClient.CreateQueueAsync(new CreateQueueRequest { QueueName = otherQueue });

        var options = new AmazonSqsOptions { ServiceUri = new Uri(ServiceAddress!) };

        try
        {
            (await QueueExistsAsync(sqsClient, matchingQueue)).Should().BeTrue();
            (await TopicExistsAsync(snsClient, matchingTopic)).Should().BeTrue();
            (await QueueExistsAsync(sqsClient, otherQueue)).Should().BeTrue();

            // Act
            var reaped = await AmazonSqsTopologyReaper.ReapByPrefixAsync(options, reapPrefix);

            // Assert
            reaped.Should().BeGreaterThanOrEqualTo(2);
            (await WaitUntilAsync(async () => !await QueueExistsAsync(sqsClient, matchingQueue)))
                .Should().BeTrue("the prefix-matching queue should be reaped");
            (await WaitUntilAsync(async () => !await TopicExistsAsync(snsClient, matchingTopic)))
                .Should().BeTrue("the prefix-matching topic should be reaped");
            (await QueueExistsAsync(sqsClient, otherQueue)).Should().BeTrue(
                "a queue under a different prefix must survive");
        }
        finally
        {
            // Hygiene — remove the surviving test queue.
            try
            {
                var url = await sqsClient.GetQueueUrlAsync(otherQueue);
                await sqsClient.DeleteQueueAsync(url.QueueUrl);
            }
            catch (QueueDoesNotExistException)
            {
                // Already gone — fine.
            }
        }
    }

    private static async Task<bool> QueueExistsAsync(AmazonSQSClient sqsClient, string name)
    {
        var response = await sqsClient.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = name });
        return (response.QueueUrls ?? []).Exists(url => url.EndsWith($"/{name}", StringComparison.Ordinal));
    }

    private static async Task<bool> TopicExistsAsync(AmazonSimpleNotificationServiceClient snsClient, string name)
    {
        var response = await snsClient.ListTopicsAsync(new ListTopicsRequest());
        return (response.Topics ?? []).Exists(topic =>
            topic.TopicArn.EndsWith($":{name}", StringComparison.Ordinal));
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }
}
