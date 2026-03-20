using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Conduit.Transport.RabbitMQ;

/// <summary>
/// Wraps the RabbitMQ channel and delivery args into a single message type
/// for <see cref="Conduit.Runtime.BrokerTransportBase{TMessage}"/>.
/// </summary>
public record RabbitMQInboundMessage(IChannel Channel, BasicDeliverEventArgs Args);
