namespace Conduit.Runtime;

/// <summary>
/// Well-known keys for the outbound header dictionary that <see cref="BrokerTransportBase{TMessage}"/>
/// assembles once per publish and hands to the transport.
/// </summary>
/// <remarks>
/// These name entries in the <b>in-process</b> dictionary, not properties on the wire. Each transport
/// maps them onto its own native property model and keeps the on-wire names it already uses, so the
/// wire format for correlation is unchanged.
/// </remarks>
public static class ConduitHeaders
{
    /// <summary>The dispatch's message identifier.</summary>
    public const string MessageId = "conduit-message-id";

    /// <summary>The identifier of the message that caused this dispatch.</summary>
    public const string CausationId = "conduit-causation-id";

    /// <summary>The conversation this dispatch belongs to.</summary>
    public const string ConversationId = "conduit-conversation-id";

    /// <summary>
    /// The <see cref="Conduit.Configuration.ConduitOptions.ApplicationName"/> of the publishing
    /// application. Used to drop an application's own events on the inbound path, because its local
    /// listeners already ran inline at publish time.
    /// </summary>
    public const string SourceApp = "conduit-source-app";
}
