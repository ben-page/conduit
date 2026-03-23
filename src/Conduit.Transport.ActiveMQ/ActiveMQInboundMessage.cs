using Amqp;

namespace Conduit.Transport.ActiveMQ;

/// <summary>
/// Wraps an AmqpNetLite <see cref="ReceiverLink"/> and <see cref="Message"/>
/// into a single type for <see cref="Conduit.Runtime.BrokerTransportBase{TMessage}"/>.
/// </summary>
public sealed class ActiveMQInboundMessage
{
    /// <summary>
    /// The receiver link that delivered this message (used for accept/release).
    /// </summary>
    public required ReceiverLink ReceiverLink { get; init; }

    /// <summary>
    /// The AMQP 1.0 message received from the broker.
    /// </summary>
    public required Message Message { get; init; }
}
