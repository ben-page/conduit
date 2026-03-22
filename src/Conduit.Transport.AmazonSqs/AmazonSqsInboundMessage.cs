using Amazon.SQS.Model;

namespace Conduit.Transport.AmazonSqs;

/// <summary>
/// Wraps the SQS message and its source queue identifier into a single message type
/// for <see cref="Conduit.Runtime.BrokerTransportBase{TMessage}"/>.
/// The queue identifier is needed for <c>DeleteMessage</c> (acknowledge) operations.
/// </summary>
public record AmazonSqsInboundMessage(Message Message, string SourceQueue);
