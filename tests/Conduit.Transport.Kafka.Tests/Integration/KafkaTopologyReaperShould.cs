using AwesomeAssertions;
using Conduit.Transport.Kafka;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Conduit.Transport.Kafka.Tests.Integration;

/// <summary>
/// Transport test matrix 7.4 (CleanupStaleResources) for Kafka: <see cref="KafkaTopologyReaper"/>
/// deletes topics whose names start with a given prefix and leaves all others intact.
/// </summary>
[TestClass]
public sealed class KafkaTopologyReaperShould : KafkaTestBase
{
    [TestMethod]
    public async Task ReapTopicsMatchingThePrefixAndLeaveOthers()
    {
        // Arrange — one topic under the reap prefix, one under a different prefix.
        using var adminClient = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = BootstrapServers! }).Build();

        var reapPrefix = $"test-reap{Guid.NewGuid():N}"[..16] + "-";
        var matching = $"{reapPrefix}conduit-deadrun";
        var other = $"test-keep{Guid.NewGuid():N}"[..16] + "-conduit-survivor";

        try
        {
            await CreateTopicAsync(adminClient, matching);
            await CreateTopicAsync(adminClient, other);
            (await TopicExistsAsync(adminClient, matching)).Should().BeTrue();
            (await TopicExistsAsync(adminClient, other)).Should().BeTrue();

            // Act
            var reaped = await KafkaTopologyReaper.ReapByPrefixAsync(BootstrapServers!, reapPrefix);

            // Assert — matching topic deleted (poll: Kafka deletion is async), other untouched.
            reaped.Should().BeGreaterThanOrEqualTo(1);
            (await WaitUntilAsync(async () => !await TopicExistsAsync(adminClient, matching)))
                .Should().BeTrue("the prefix-matching topic should be reaped");
            (await TopicExistsAsync(adminClient, other)).Should().BeTrue(
                "a topic under a different prefix must survive");
        }
        finally
        {
            // Hygiene — remove the surviving test topic (and matching, if reap somehow missed it).
            try
            {
                await adminClient.DeleteTopicsAsync([other, matching]);
            }
            catch (DeleteTopicsException)
            {
                // Already gone — fine.
            }
        }
    }

    private static async Task CreateTopicAsync(IAdminClient adminClient, string name)
    {
        try
        {
            await adminClient.CreateTopicsAsync(
            [
                new TopicSpecification { Name = name, NumPartitions = 1, ReplicationFactor = 1 }
            ]);
        }
        catch (CreateTopicsException ex)
            when (ex.Results.Count > 0 && ex.Results[0].Error.Code == ErrorCode.TopicAlreadyExists)
        {
            // Already present — fine.
        }
    }

    private static Task<bool> TopicExistsAsync(IAdminClient adminClient, string name)
    {
        var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(10));
        var exists = metadata.Topics.Exists(topic =>
            topic.Topic == name && topic.Error.Code == ErrorCode.NoError);
        return Task.FromResult(exists);
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
