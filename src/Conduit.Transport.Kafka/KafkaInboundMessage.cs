using Confluent.Kafka;

namespace Conduit.Transport.Kafka;

/// <summary>
/// Wraps a Kafka ConsumeResult for processing by BrokerTransportBase.
/// </summary>
public sealed class KafkaInboundMessage
{
    public required ConsumeResult<string, byte[]> ConsumeResult { get; init; }
}
