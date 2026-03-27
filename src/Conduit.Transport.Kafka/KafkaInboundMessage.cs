using Confluent.Kafka;

namespace Conduit.Transport.Kafka;

/// <summary>
/// Wraps a Kafka ConsumeResult for processing by BrokerTransportBase.
/// </summary>
public sealed class KafkaInboundMessage
{
    /// <summary>The underlying Kafka consume result containing the message and metadata.</summary>
    public required ConsumeResult<string, byte[]> ConsumeResult { get; init; }
}
