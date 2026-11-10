using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Conduit.Transport.RabbitMQ;

/// <summary>
/// Wraps the RabbitMQ channel and delivery args into a single message type
/// for <see cref="Conduit.Runtime.BrokerTransportBase{TMessage}"/>. The transport builds it with its own
/// copy of the delivery's body, since the client reuses the buffer it received the body in.
/// </summary>
public record RabbitMQInboundMessage(IChannel Channel, BasicDeliverEventArgs Args);
