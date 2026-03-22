using Confluent.Kafka;

namespace Conduit.Transport.Kafka;

/// <summary>
/// Wraps a Kafka ConsumeResult for processing by BrokerTransportBase.
/// Includes retry count for in-process retry tracking on event listener failures.
/// </summary>
public sealed class KafkaInboundMessage
{
    public required ConsumeResult<string, byte[]> ConsumeResult { get; init; }
    public int RetryCount { get; set; }
}
