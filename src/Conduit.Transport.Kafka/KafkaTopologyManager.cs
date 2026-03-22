using System.Globalization;
using Conduit.Configuration;
using Conduit.Runtime;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.Kafka;

/// <summary>
/// Creates Kafka topics for the transport. All creations are idempotent —
/// if the topic already exists, the call is a no-op (CreateTopicsException with
/// ErrorCode.TopicAlreadyExists is swallowed).
/// </summary>
internal sealed class KafkaTopologyManager
{
    private static readonly Action<ILogger, string, Exception?> LogCreatingTopic =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(200, "CreatingTopic"),
            "Creating Kafka topic '{TopicName}'");

    private static readonly Action<ILogger, string, Exception?> LogTopicAlreadyExists =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(201, "TopicAlreadyExists"),
            "Kafka topic '{TopicName}' already exists");

    private readonly KafkaOptions options;
    private readonly ConduitOptions conduitOptions;
    private readonly ILogger logger;

    public KafkaTopologyManager(KafkaOptions options, ConduitOptions conduitOptions, ILogger logger)
    {
        this.options = options;
        this.conduitOptions = conduitOptions;
        this.logger = logger;
    }

    /// <summary>
    /// Ensures all required topics exist: shared topic, reply topic, and dead-letter topic.
    /// </summary>
    public async Task EnsureTopologyAsync(CancellationToken cancellationToken)
    {
        using var adminClient = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = this.options.BootstrapServers })
            .Build();

        var sharedTopicName = TopologyNaming.CreateSharedResourceName(this.conduitOptions, 249);
        var replyTopicName = TopologyNaming.CreateAppResourceName(this.conduitOptions, "replies", 249);
        var deadLetterTopicName = TopologyNaming.CreateAppResourceName(this.conduitOptions, "dead-letter", 249);

        var retentionMs = this.GetRetentionMs();
        var topicConfigs = retentionMs is not null
            ? new Dictionary<string, string> { ["retention.ms"] = retentionMs }
            : new Dictionary<string, string>();

        await this.CreateTopicIfNotExistsAsync(adminClient, sharedTopicName, numPartitions: 6, topicConfigs);
        cancellationToken.ThrowIfCancellationRequested();

        await this.CreateTopicIfNotExistsAsync(adminClient, replyTopicName, numPartitions: 1, topicConfigs);
        cancellationToken.ThrowIfCancellationRequested();

        await this.CreateTopicIfNotExistsAsync(adminClient, deadLetterTopicName, numPartitions: 1, topicConfigs);
    }

    /// <summary>
    /// Returns the list of topics managed by this transport (for cleanup task).
    /// </summary>
    public IReadOnlyList<string> GetManagedTopicNames()
    {
        var sharedTopicName = TopologyNaming.CreateSharedResourceName(this.conduitOptions, 249);
        var replyTopicName = TopologyNaming.CreateAppResourceName(this.conduitOptions, "replies", 249);
        var deadLetterTopicName = TopologyNaming.CreateAppResourceName(this.conduitOptions, "dead-letter", 249);
        return [sharedTopicName, replyTopicName, deadLetterTopicName];
    }

    private async Task CreateTopicIfNotExistsAsync(
        IAdminClient adminClient,
        string topicName,
        int numPartitions,
        Dictionary<string, string> configs)
    {
        LogCreatingTopic(this.logger, topicName, null);

        try
        {
            await adminClient.CreateTopicsAsync(
            [
                new TopicSpecification
                {
                    Name = topicName,
                    NumPartitions = numPartitions,
                    ReplicationFactor = 1,
                    Configs = configs,
                }
            ]);
        }
        catch (CreateTopicsException ex)
            when (ex.Results.Count > 0 && ex.Results[0].Error.Code == ErrorCode.TopicAlreadyExists)
        {
            LogTopicAlreadyExists(this.logger, topicName, null);
        }
    }

    private string? GetRetentionMs() => this.conduitOptions.TopologyMode switch
    {
        TopologyMode.Developer => ((long)TimeSpan.FromHours(4).TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
        TopologyMode.Test => ((long)TimeSpan.FromMinutes(5).TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
        TopologyMode.Production => null,
        _ => null,
    };
}
