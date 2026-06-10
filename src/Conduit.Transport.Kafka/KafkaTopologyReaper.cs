using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Conduit.Transport.Kafka;

/// <summary>
/// Deletes Kafka topics and consumer groups whose names start with a given prefix. Used to reclaim a
/// test project's resources (named under a stable <c>test-{hash}-</c> prefix) between runs, and
/// available to applications that want their own prefix-scoped cleanup pass. Best-effort: per-batch
/// delete failures are swallowed and the call never throws on a single failure. Returns the number
/// of resources deleted.
/// </summary>
public static class KafkaTopologyReaper
{
    /// <summary>
    /// Deletes all Kafka topics and consumer groups whose names start with <paramref name="prefix"/>.
    /// </summary>
    public static async Task<int> ReapByPrefixAsync(
        string bootstrapServers, string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapServers);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        cancellationToken.ThrowIfCancellationRequested();

        using var adminClient = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

        var reaped = 0;

        var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(10));
        var topics = metadata.Topics
            .Select(topic => topic.Topic)
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        if (topics.Count > 0)
        {
            try
            {
                await adminClient.DeleteTopicsAsync(topics);
                reaped += topics.Count;
            }
            catch (DeleteTopicsException)
            {
                // Best-effort: a topic mid-delete/in-use is left for the next pass.
            }
        }

        var groups = await adminClient.ListConsumerGroupsAsync();
        var groupIds = groups.Valid
            .Select(group => group.GroupId)
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        if (groupIds.Count > 0)
        {
            try
            {
                await adminClient.DeleteGroupsAsync(groupIds);
                reaped += groupIds.Count;
            }
            catch (DeleteGroupsException)
            {
                // Best-effort: a still-active group is left for the next pass.
            }
        }

        return reaped;
    }
}
